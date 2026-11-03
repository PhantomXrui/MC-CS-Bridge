using System.IO.Pipes;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
var root=Environment.GetEnvironmentVariable("MCCS_ROOT");
if(string.IsNullOrWhiteSpace(root)||!Path.IsPathFullyQualified(root))
    throw new InvalidOperationException("Set MCCS_ROOT to an absolute bridge workspace path before launching the relay.");
root=Path.GetFullPath(root);
var token=File.ReadAllText(Path.Combine(root,"runtime/bridge-token.txt")).Trim();
Console.WriteLine("Local pipe <-> Minecraft 127.0.0.1:25599 relay. No game network rules changed.");
while(true) {
    try {
        using var pipe=new NamedPipeServerStream("MC-CS-Bridge-v1",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.WaitForConnectionAsync();
        using var ws=new ClientWebSocket();ws.Options.SetRequestHeader("Authorization","Bearer "+token);
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:25599/"),CancellationToken.None);
        using var stop=new CancellationTokenSource();
        using var reader=new StreamReader(pipe,new UTF8Encoding(false),false,65536,true);
        using var writer=new StreamWriter(pipe,new UTF8Encoding(false),65536,true){AutoFlush=true};
        Console.WriteLine(DateTime.Now.ToString("O")+" connected");
        var toMinecraft=Task.Run(async()=>{while(true){var line=await reader.ReadLineAsync(stop.Token);if(line==null)return;var bytes=Encoding.UTF8.GetBytes(line);await ws.SendAsync(bytes,WebSocketMessageType.Text,true,stop.Token);}},stop.Token);
        var toCS=Task.Run(async()=>{var buffer=new byte[65536];using var message=new MemoryStream();while(true){var r=await ws.ReceiveAsync(buffer,stop.Token);if(r.MessageType==WebSocketMessageType.Close)return;message.Write(buffer,0,r.Count);if(message.Length>1048576)throw new InvalidDataException("Message too large");if(r.EndOfMessage){using var json=JsonDocument.Parse(message.ToArray());message.SetLength(0);var line=JsonSerializer.Serialize(json.RootElement);await writer.WriteLineAsync(line.AsMemory(),stop.Token);}}},stop.Token);
        await Task.WhenAny(toMinecraft,toCS);stop.Cancel();
        try{await Task.WhenAll(toMinecraft,toCS);}catch(OperationCanceledException){}
    }catch(Exception e){Console.WriteLine(DateTime.Now.ToString("O")+" "+e.GetType().Name+": "+e.Message);}
    await Task.Delay(1000);
}
