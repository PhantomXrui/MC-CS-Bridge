using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
const string root=@"D:\MC-CS-Bridge";
if(args.Length<1||args.Length>2)throw new ArgumentException("Pass a JSON command path and optional response capture path.");
var token=File.ReadAllText(Path.Combine(root,"runtime/bridge-token.txt")).Trim();
using var ws=new ClientWebSocket();ws.Options.SetRequestHeader("Authorization","Bearer "+token);
using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
await ws.ConnectAsync(new Uri("ws://127.0.0.1:25599/"),deadline.Token);
byte[] buffer=new byte[65536];var greeting=await ws.ReceiveAsync(buffer,deadline.Token);
Console.WriteLine(Encoding.UTF8.GetString(buffer,0,greeting.Count));
var responses=new System.Collections.Concurrent.ConcurrentQueue<JsonElement>();
using var receiving=new CancellationTokenSource();
var receiveTask=Task.Run(async()=>{
 var bytes=new byte[65536];using var message=new MemoryStream();
 try{while(!receiving.IsCancellationRequested){var r=await ws.ReceiveAsync(bytes,receiving.Token);if(r.MessageType==WebSocketMessageType.Close)break;message.Write(bytes,0,r.Count);if(message.Length>1048576)throw new InvalidDataException();if(!r.EndOfMessage)continue;using var doc=JsonDocument.Parse(message.ToArray());message.SetLength(0);var root=doc.RootElement;if(root.TryGetProperty("t",out var t)&&t.GetString() is "uistate" or "dancestate" or "clearallmobs_result" or "mobdamage" or "proj" or "actorhit" or "actorinteractionstate" or "blockinspect" or "actorbreedack" or "actorcontrolack")responses.Enqueue(root.Clone());}}
 catch(OperationCanceledException){}catch(WebSocketException){}
});
using var document=JsonDocument.Parse(File.ReadAllText(args[0]));
var commands=document.RootElement.ValueKind==JsonValueKind.Array?document.RootElement.EnumerateArray().ToArray():new[]{document.RootElement};
foreach(var item in commands){
 if(item.TryGetProperty("wait_ms",out var delay)){await Task.Delay(Math.Clamp(delay.GetInt32(),0,4000));continue;}
 var command=Encoding.UTF8.GetBytes(item.GetRawText());
 await ws.SendAsync(command,WebSocketMessageType.Text,true,deadline.Token);
}
await Task.Delay(500);
await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"Done",deadline.Token);
receiving.Cancel();await receiveTask;
if(args.Length==2)File.WriteAllText(args[1],JsonSerializer.Serialize(responses.ToArray(),new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("Sent local command.");
