using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Brainmind.Runtime
{
    // Per-turn wire state. Protocol discovery never replays a completed response or tool.
    internal sealed class ModelWireProtocol
    {
        private const int MaxBytes = 2 * 1024 * 1024;
        private readonly ModelConfig config;
        private readonly bool automatic;
        private bool attemptedFallback, pinned;
        internal string Protocol { get; private set; }

        internal ModelWireProtocol(ModelConfig config)
        {
            this.config = config;
            Protocol = config.NormalizedProtocol();
            automatic = Protocol == "auto";
            if(automatic)
            {
                Uri uri = config.CompletionEndpoint(); // Validate credentials and URL before network use.
                string path = new Uri(config.BaseUrl.Trim()).AbsolutePath.TrimEnd('/');
                if(path.EndsWith("/responses",StringComparison.OrdinalIgnoreCase)) Protocol = "responses";
                else if(path.EndsWith("/messages",StringComparison.OrdinalIgnoreCase)) Protocol = "anthropic";
                else if(path.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase)) Protocol = "chat_completions";
                else Protocol = uri.IsLoopback && (config.ApiKey ?? "").Trim() == "PROXY_MANAGED" ? "anthropic" : "chat_completions";
            }
            config.EndpointFor(Protocol);
        }

        internal async Task<JsonElement> RequestAsync(HttpClient http,List<object> messages,object[] tools,object choice,CancellationToken token)
        {
            while(true)
            {
                Uri endpoint = config.EndpointFor(Protocol);
                string payload = BuildPayload(messages,tools,choice);
                using(HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post,endpoint))
                {
                    request.Content = new StringContent(payload,Encoding.UTF8,"application/json");
                    if(!String.IsNullOrWhiteSpace(config.ApiKey))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",config.ApiKey.Trim());
                        if(Protocol=="anthropic") request.Headers.TryAddWithoutValidation("x-api-key",config.ApiKey.Trim());
                    }
                    if(Protocol=="anthropic") request.Headers.TryAddWithoutValidation("anthropic-version","2023-06-01");
                    using(HttpResponseMessage response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if(!response.IsSuccessStatusCode && automatic && !pinned && !attemptedFallback && (status==404 || status==405))
                        {
                            attemptedFallback = true;
                            Protocol = Protocol=="chat_completions" ? "responses" : Protocol=="responses" ? "chat_completions" : "responses";
                            continue;
                        }
                        byte[] body = await ReadBody(response,token).ConfigureAwait(false);
                        if(!response.IsSuccessStatusCode)
                            throw new InvalidOperationException("模型接口返回 HTTP " + status + "。" + ErrorText(body));
                        string text = new UTF8Encoding(false,true).GetString(body);
                        bool sse = (response.Content.Headers.ContentType!=null && response.Content.Headers.ContentType.MediaType=="text/event-stream")
                            || text.TrimStart().StartsWith("event:",StringComparison.Ordinal) || text.TrimStart().StartsWith("data:",StringComparison.Ordinal);
                        JsonElement result;
                        try
                        {
                            if(sse) result = ParseEvents(text);
                            else using(JsonDocument document = JsonDocument.Parse(body)) result = Normalize(document.RootElement);
                        }
                        catch(JsonException) { throw new InvalidOperationException("模型接口返回了无效或不完整的 JSON，未执行本次响应中的工具。"); }
                        pinned = true;
                        return result;
                    }
                }
            }
        }

        private static async Task<byte[]> ReadBody(HttpResponseMessage response,CancellationToken token)
        {
            if(response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value>MaxBytes)
                throw new InvalidOperationException("Model response exceeded the 2 MiB limit.");
            using(Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            using(MemoryStream buffer = new MemoryStream())
            {
                byte[] block = new byte[8192]; int count;
                while((count=await source.ReadAsync(block,0,block.Length,token).ConfigureAwait(false))>0)
                {
                    if(buffer.Length+count>MaxBytes) throw new InvalidOperationException("Model response exceeded the 2 MiB limit.");
                    buffer.Write(block,0,count);
                }
                return buffer.ToArray();
            }
        }

        private string ErrorText(byte[] body)
        {
            string message = "请检查模型、协议和路由配置。";
            try
            {
                using(JsonDocument document = JsonDocument.Parse(body))
                {
                    JsonElement error,value;
                    if(document.RootElement.TryGetProperty("error",out error) && error.ValueKind==JsonValueKind.Object)
                    {
                        string code = error.TryGetProperty("code",out value) && value.ValueKind==JsonValueKind.String ? value.GetString() : "";
                        string detail = error.TryGetProperty("message",out value) && value.ValueKind==JsonValueKind.String ? value.GetString() : "";
                        if(Regex.IsMatch(code ?? "","^[A-Za-z0-9_.-]{1,80}$")) message = "["+code+"] "+detail;
                    }
                }
            }
            catch(JsonException) { }
            if(!String.IsNullOrEmpty(config.ApiKey)) message = message.Replace(config.ApiKey,"[redacted]");
            message = Regex.Replace(message,"(?i)\\bsk-[A-Za-z0-9_-]{12,}|(?:Bearer\\s+)[A-Za-z0-9_.-]+","[redacted]");
            message = Regex.Replace(message,"https?://[^\\s]+","[endpoint]");
            return message.Length>400 ? message.Substring(0,400) : message;
        }

        private string BuildPayload(List<object> messages,object[] tools,object choice)
        {
            List<JsonElement> source = new List<JsonElement>();
            foreach(object item in messages) source.Add(Element(item));
            if(Protocol=="chat_completions")
            {
                List<object> clean = new List<object>();
                foreach(JsonElement item in source)
                {
                    Dictionary<string,object> message = new Dictionary<string,object>();
                    foreach(JsonProperty field in item.EnumerateObject())
                        if(field.Name=="role" || field.Name=="content" || field.Name=="tool_calls" || field.Name=="tool_call_id" || field.Name=="name") message[field.Name] = field.Value.Clone();
                    clean.Add(message);
                }
                return JsonSerializer.Serialize(new {model=config.Model.Trim(),messages=clean,tools=tools,tool_choice=choice,parallel_tool_calls=false,stream=false});
            }
            List<string> system = new List<string>();
            List<object> input = new List<object>();
            foreach(JsonElement item in source)
            {
                string role = item.GetProperty("role").GetString();
                JsonElement raw;
                if(role=="system") {system.Add(Content(item));continue;}
                if(Protocol=="responses")
                {
                    if(role=="assistant" && item.TryGetProperty("_brainmind_response_output",out raw))
                        foreach(JsonElement output in raw.EnumerateArray()) input.Add(output.Clone());
                    else if(role=="tool") input.Add(new {type="function_call_output",call_id=item.GetProperty("tool_call_id").GetString(),output=Content(item)});
                    else
                    {
                        string text=Content(item);
                        if(!String.IsNullOrEmpty(text)) input.Add(new {role=role,content=new object[]{new {type=role=="assistant" ? "output_text" : "input_text",text=text}}});
                    }
                }
                else
                {
                    List<object> blocks = new List<object>();
                    if(role=="assistant" && item.TryGetProperty("_brainmind_anthropic_content",out raw))
                        foreach(JsonElement block in raw.EnumerateArray()) blocks.Add(block.Clone());
                    else if(role=="tool")
                    {
                        string output=Content(item); bool failed=false;
                        try {using(JsonDocument value=JsonDocument.Parse(output)) {JsonElement ok;failed=value.RootElement.TryGetProperty("success",out ok) && ok.ValueKind==JsonValueKind.False;}} catch(JsonException) { }
                        blocks.Add(new {type="tool_result",tool_use_id=item.GetProperty("tool_call_id").GetString(),content=output,is_error=failed});
                        role="user";
                    }
                    else if(!String.IsNullOrEmpty(Content(item))) blocks.Add(new {type="text",text=Content(item)});
                    if(blocks.Count>0) AddAnthropicMessage(input,role,blocks);
                }
            }
            List<object> declared = new List<object>();
            foreach(object item in tools)
            {
                JsonElement function=Element(item).GetProperty("function");
                if(Protocol=="responses") declared.Add(new {type="function",name=function.GetProperty("name").GetString(),description=function.GetProperty("description").GetString(),parameters=function.GetProperty("parameters").Clone(),strict=false});
                else declared.Add(new {name=function.GetProperty("name").GetString(),description=function.GetProperty("description").GetString(),input_schema=function.GetProperty("parameters").Clone()});
            }
            JsonElement selected=Element(choice); object selectedTool;
            if(selected.ValueKind==JsonValueKind.String)
                selectedTool=Protocol=="responses" ? (object)selected.GetString() : selected.GetString()=="none" ? (object)new {type="none"} : new {type=selected.GetString(),disable_parallel_tool_use=true};
            else
            {
                string name=selected.GetProperty("function").GetProperty("name").GetString();
                selectedTool=Protocol=="responses" ? (object)new {type="function",name=name} : new {type="tool",name=name,disable_parallel_tool_use=true};
            }
            if(Protocol=="responses") return JsonSerializer.Serialize(new {model=config.Model.Trim(),instructions=String.Join("\n\n",system.ToArray()),input=input,tools=declared,tool_choice=selectedTool,parallel_tool_calls=false,stream=true,store=false,include=new string[]{"reasoning.encrypted_content"}});
            return JsonSerializer.Serialize(new {model=config.Model.Trim(),system=String.Join("\n\n",system.ToArray()),messages=input,tools=declared,tool_choice=selectedTool,max_tokens=8192,stream=false});
        }

        private static void AddAnthropicMessage(List<object> input,string role,List<object> blocks)
        {
            Dictionary<string,object> previous = input.Count>0 ? input[input.Count-1] as Dictionary<string,object> : null;
            if(previous!=null && Convert.ToString(previous["role"])==role) ((List<object>)previous["content"]).AddRange(blocks);
            else input.Add(new Dictionary<string,object>{{"role",role},{"content",blocks}});
        }
        private static JsonElement Element(object value) {using(JsonDocument document=JsonDocument.Parse(JsonSerializer.Serialize(value))) return document.RootElement.Clone();}
        private static string Content(JsonElement value) {JsonElement content;return value.TryGetProperty("content",out content) && content.ValueKind==JsonValueKind.String ? content.GetString() : "";}

        private JsonElement Normalize(JsonElement root)
        {
            if(root.ValueKind!=JsonValueKind.Object) throw new InvalidOperationException("模型接口未返回消息对象。");
            if(Protocol=="chat_completions")
            {
                JsonElement choices,message;
                if(!root.TryGetProperty("choices",out choices) || choices.ValueKind!=JsonValueKind.Array || choices.GetArrayLength()==0 || !choices[0].TryGetProperty("message",out message) || message.ValueKind!=JsonValueKind.Object)
                    throw new InvalidOperationException("The endpoint did not return a Chat Completions message.");
                return message.Clone();
            }
            List<object> calls=new List<object>(); List<string> text=new List<string>();
            JsonElement blocks,status;
            if(Protocol=="responses")
            {
                if(!root.TryGetProperty("status",out status) || status.GetString()!="completed" || !root.TryGetProperty("output",out blocks) || blocks.ValueKind!=JsonValueKind.Array)
                    throw new InvalidOperationException("Responses 响应未完整完成，未执行其中的工具。");
                foreach(JsonElement item in blocks.EnumerateArray())
                {
                    string type=item.GetProperty("type").GetString();
                    if(type=="function_call") calls.Add(new {id=item.GetProperty("call_id").GetString(),type="function",function=new {name=item.GetProperty("name").GetString(),arguments=item.GetProperty("arguments").GetString()}});
                    else if(type=="message") foreach(JsonElement content in item.GetProperty("content").EnumerateArray())
                    {
                        string kind=content.GetProperty("type").GetString();
                        if(kind=="output_text") text.Add(content.GetProperty("text").GetString());
                        else if(kind=="refusal") text.Add(content.GetProperty("refusal").GetString());
                    }
                    else if(type!="reasoning") throw new InvalidOperationException("Responses 返回了未支持的输出类型，未执行工具。");
                }
                return Element(new {role="assistant",content=String.Join("\n",text.ToArray()),tool_calls=calls,_brainmind_response_output=blocks.Clone()});
            }
            if(!root.TryGetProperty("stop_reason",out status) || (status.GetString()!="end_turn" && status.GetString()!="tool_use" && status.GetString()!="stop_sequence")
                || !root.TryGetProperty("content",out blocks) || blocks.ValueKind!=JsonValueKind.Array)
                throw new InvalidOperationException("Anthropic 消息未完整完成，未执行其中的工具。");
            foreach(JsonElement item in blocks.EnumerateArray())
            {
                string type=item.GetProperty("type").GetString();
                if(type=="text") text.Add(item.GetProperty("text").GetString());
                else if(type=="tool_use") calls.Add(new {id=item.GetProperty("id").GetString(),type="function",function=new {name=item.GetProperty("name").GetString(),arguments=item.GetProperty("input").GetRawText()}});
                else if(type!="thinking" && type!="redacted_thinking") throw new InvalidOperationException("Anthropic 返回了未支持的内容类型，未执行工具。");
            }
            return Element(new {role="assistant",content=String.Join("\n",text.ToArray()),tool_calls=calls,_brainmind_anthropic_content=blocks.Clone()});
        }

        private JsonElement ParseEvents(string body)
        {
            if(Protocol=="chat_completions") throw new InvalidOperationException("Chat Completions 返回了非预期流；请选择与路由匹配的协议，未执行工具。");
            JsonElement? completed=null; Dictionary<string,object> message=null;
            SortedDictionary<int,Dictionary<string,object>> blocks=new SortedDictionary<int,Dictionary<string,object>>();
            Dictionary<int,StringBuilder> arguments=new Dictionary<int,StringBuilder>(); HashSet<int> open=new HashSet<int>(); bool stopped=false;
            foreach(string data in EventData(body))
            {
                if(data=="[DONE]") continue;
                using(JsonDocument document=JsonDocument.Parse(data))
                {
                    JsonElement item=document.RootElement;string type=item.GetProperty("type").GetString();
                    if(type=="error" || type=="response.failed" || type=="response.incomplete") throw new InvalidOperationException("模型流报告失败或不完整，未执行本次响应中的工具。");
                    if(Protocol=="responses")
                    {
                        if(type=="response.completed")
                        {
                            if(completed.HasValue) throw new InvalidOperationException("模型流含重复完成事件，未执行工具。");
                            completed=item.GetProperty("response").Clone();
                        }
                        continue;
                    }
                    if(type=="message_start")
                    {
                        if(message!=null) throw new InvalidOperationException("模型流重复开始，未执行工具。");
                        message=Object(item.GetProperty("message"));
                    }
                    else if(type=="content_block_start")
                    {
                        int index=item.GetProperty("index").GetInt32();
                        if(blocks.ContainsKey(index)) throw new InvalidOperationException("模型流内容索引重复，未执行工具。");
                        blocks[index]=Object(item.GetProperty("content_block"));open.Add(index);
                    }
                    else if(type=="content_block_delta")
                    {
                        int index=item.GetProperty("index").GetInt32();
                        if(!open.Contains(index)) throw new InvalidOperationException("模型流内容顺序不完整，未执行工具。");
                        JsonElement delta=item.GetProperty("delta");string kind=delta.GetProperty("type").GetString();
                        if(kind=="input_json_delta")
                        {
                            StringBuilder parts;if(!arguments.TryGetValue(index,out parts)) arguments[index]=parts=new StringBuilder();
                            parts.Append(delta.GetProperty("partial_json").GetString());
                        }
                        else if(kind=="text_delta" || kind=="thinking_delta" || kind=="signature_delta")
                        {
                            string key=kind=="text_delta" ? "text" : kind=="thinking_delta" ? "thinking" : "signature";
                            object prior;blocks[index].TryGetValue(key,out prior);blocks[index][key]=Convert.ToString(prior)+delta.GetProperty(key).GetString();
                        }
                        else throw new InvalidOperationException("模型流含未支持的增量，未执行工具。");
                    }
                    else if(type=="content_block_stop")
                    {
                        int index=item.GetProperty("index").GetInt32();
                        if(!open.Remove(index)) throw new InvalidOperationException("模型流内容未正确开始，未执行工具。");
                        StringBuilder parts;
                        if(arguments.TryGetValue(index,out parts) && parts.Length>0)
                            using(JsonDocument value=JsonDocument.Parse(parts.ToString())) blocks[index]["input"]=value.RootElement.Clone();
                    }
                    else if(type=="message_delta")
                    {
                        if(message==null) throw new InvalidOperationException("模型流缺少消息起点，未执行工具。");
                        JsonElement reason;if(item.GetProperty("delta").TryGetProperty("stop_reason",out reason)) message["stop_reason"]=reason.Clone();
                    }
                    else if(type=="message_stop")
                    {
                        if(stopped) throw new InvalidOperationException("模型流重复结束，未执行工具。");
                        stopped=true;
                    }
                }
            }
            if(Protocol=="responses")
            {
                if(!completed.HasValue) throw new InvalidOperationException("Responses 流在完成事件前断开，未执行工具。");
                return Normalize(completed.Value);
            }
            if(!stopped || message==null || open.Count>0) throw new InvalidOperationException("Anthropic 流在完整结束前断开，未执行工具。");
            message["content"]=new List<Dictionary<string,object>>(blocks.Values);
            return Normalize(Element(message));
        }
        private static Dictionary<string,object> Object(JsonElement value)
        {
            Dictionary<string,object> result=new Dictionary<string,object>();
            foreach(JsonProperty field in value.EnumerateObject()) result[field.Name]=field.Value.Clone();
            return result;
        }
        private static IEnumerable<string> EventData(string body)
        {
            StringBuilder data=new StringBuilder();
            using(StringReader reader=new StringReader(body))
            {
                string line;
                while((line=reader.ReadLine())!=null)
                {
                    if(line.Length==0)
                    {
                        if(data.Length>0) {yield return data.ToString();data.Clear();}
                    }
                    else if(line.StartsWith("data:",StringComparison.Ordinal))
                    {
                        if(data.Length>0) data.Append('\n');
                        string value=line.Substring(5);if(value.StartsWith(" ",StringComparison.Ordinal)) value=value.Substring(1);data.Append(value);
                    }
                }
            }
            // An unterminated final SSE event cannot authorize a tool.
            if(data.Length>0) throw new InvalidOperationException("模型流最后一个事件被截断，未执行工具。");
        }
    }
}
