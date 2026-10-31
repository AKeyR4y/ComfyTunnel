using ComfyTunnel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

try
{
int passed = 0;
async Task Test(string name, Func<Task> run) { await run(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Reject(string text) { try { SshCommandParser.Parse(text); } catch (FormatException) { return; } throw new Exception("Accepted invalid command"); }
var root = Path.GetFullPath(args.Length > 0 ? args[0] : "test-results"); Directory.CreateDirectory(root);
await Test("presentation: six states retain cancel actions and explicit retry", () => {
 Check(ConnectionPresentation.From(TunnelState.Idle,false).Action=="添加服务器");
 Check(ConnectionPresentation.From(TunnelState.Idle,true).Action=="连接");
 foreach(var state in new[]{TunnelState.Connecting,TunnelState.Retrying}) { var view=ConnectionPresentation.From(state,true);Check(view.Busy && view.Action.Contains("取消")); }
 Check(ConnectionPresentation.From(TunnelState.Connected,true) is { Action:"断开", Busy:false });
 Check(ConnectionPresentation.From(TunnelState.Failed,true) is { Action:"重试", Tone:"Error", Busy:false });
 return Task.CompletedTask;
});
await Test("forwarding summary: off, configured ports, allocated port and reconnect", () => {
 Check(ForwardingSummary.Format(false,"host",8188,18188,null,TunnelState.Connected)=="仅 SSH · 不开放本地端口");
 Check(ForwardingSummary.Format(true,"127.0.0.1",8188,18188,null,TunnelState.Idle).Contains("127.0.0.1:18188 → 127.0.0.1:8188 · 待连接"));
 Check(ForwardingSummary.Format(true,"::1",9000,18188,49152,TunnelState.Connected)=="127.0.0.1:49152 → [::1]:9000");
 Check(ForwardingSummary.Format(true,"host",8188,18188,49152,TunnelState.Retrying).EndsWith("待恢复"));
 return Task.CompletedTask;
});
await Test("field validation: simultaneous errors mapped to fields, optional drafts", () => {
 var errors=ProfileFields.Validate("https://bad.example","0","bad user","127.0.0.1","x","65536",true,"missing-test-key",true);
 Check(new[]{"Host","SshPort","User","TargetPort","LocalPort","KeyPath"}.All(errors.ContainsKey));
 Check(!errors.ContainsKey("TargetHost"));
 Check(ProfileFields.Validate("","22","","127.0.0.1","8188","18188",false,"",false).Count==0);
 Check(ProfileFields.Validate("gpu.example","2222","ubuntu","::1","8188","18188",false,"",true).Count==0);
 return Task.CompletedTask;
});
await Test("optional Comfy: default off, legacy off, disabled fields do not block SSH", () => {
 Check(!new Profile().EnableComfy);
 var old=System.Text.Json.JsonSerializer.Deserialize<Profile>("{\"Host\":\"legacy.example\",\"User\":\"ubuntu\"}")!;Check(!old.EnableComfy);
 Check(ProfileFields.Validate("gpu.example","22","ubuntu","bad target","invalid","0",false,"",true,false).Count==0);
 (old with{TargetHost="bad target",TargetPort=0,LocalPort=0}).Validate();
 return Task.CompletedTask;
});
await Test("parser: quoted Windows paths, IPv6, order, missing username", () => {
 var a = SshCommandParser.Parse("ssh -i \"C:\\Keys\\gpu key\" -p12345 ubuntu@gpu.example.com");
 Check(a.Port==12345 && a.KeyPath==@"C:\Keys\gpu key" && a.User=="ubuntu");
 Check(SshCommandParser.Parse("ssh.exe -l root [::1] -p 23").Host=="::1");
 Check(SshCommandParser.Parse("ssh host.example").User==""); return Task.CompletedTask;
});
await Test("parser: reject shell expressions, unknown/repeated options and remote commands", () => {
 foreach(var bad in new[]{"ssh x; whoami","ssh x | more","ssh $(cmd)","ssh -J jump x","ssh -o ProxyCommand=x x","ssh -p 22 -p 23 x","ssh -l u u@x","ssh -p 0 x","ssh -p 65536 x","ssh -i \"bad x","ssh x id","ssh -p","ssh root@","ssh ::1"}) Reject(bad);
 return Task.CompletedTask;
});
var storage = new Storage(Path.Combine(root,"storage"));
await Test("DPAPI and configuration: roundtrip, no plaintext, explicit forget", () => {
 var p = new Profile { Host="example.com",User="user",Secret="test-not-a-real-secret",RememberSecret=true };
 storage.SaveProfile(p); Check(storage.LoadProfile().Secret==p.Secret);
 Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(storage.DirectoryPath,"connections.dat"))).Contains(p.Secret));
 storage.SaveProfile(p with { RememberSecret=false }); Check(storage.LoadProfile().Secret=="");
 return Task.CompletedTask;
});
await Test("multi-profile vault: encrypt metadata, keep credentials separated, forget and remove", () => {
 var vault=new Storage(Path.Combine(root,"multi"));
 var a=new SavedConnection{Name="Private GPU Alpha",Profile=new(){Host="private-alpha.example",User="alpha-user",Secret="alpha-secret",RememberSecret=true}};
 Check(!a.ToString().Contains("alpha-secret") && !a.Profile.ToString().Contains("alpha-secret"));
 var b=new SavedConnection{Name="Private GPU Beta",Profile=new(){Host="private-beta.example",User="beta-user",Secret="beta-secret",RememberSecret=false,TargetPort=9999,EnableComfy=true}};
 vault.SaveConnections(new(){SelectedId=b.Id,Connections=[a,b]});
 var loaded=vault.LoadConnections();Check(loaded.SelectedId==b.Id && loaded.Connections.Count==2);
 Check(loaded.Connections[0].Profile.Secret=="alpha-secret" && loaded.Connections[1].Profile.Secret=="");
 Check(loaded.Connections[1].Profile.TargetPort==9999);
 Check(!loaded.Connections[0].Profile.EnableComfy && loaded.Connections[1].Profile.EnableComfy);
 string disk=Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(vault.DirectoryPath,"connections.dat")));
 foreach(var secret in new[]{a.Name,b.Name,a.Profile.Host,b.Profile.Host,a.Profile.User,b.Profile.User,"alpha-secret","beta-secret"})Check(!disk.Contains(secret));
 vault.SaveConnections(loaded with{Connections=[loaded.Connections[1]]});Check(vault.LoadConnections().Connections.Single().Id==b.Id);
 return Task.CompletedTask;
});
await Test("legacy migration: preserve metadata and trust, remove old plaintext files", () => {
 var vault=new Storage(Path.Combine(root,"legacy"));Directory.CreateDirectory(vault.DirectoryPath);
 File.WriteAllText(Path.Combine(vault.DirectoryPath,"profile.json"),System.Text.Json.JsonSerializer.Serialize(new Profile{Host="legacy.example",User="legacy-user"}));
 File.WriteAllText(Path.Combine(vault.DirectoryPath,"known-hosts.json"),"{\"[legacy.example]:22\":\"SHA256:trusted\"}");
 Check(vault.LoadConnections().Connections.Single().Profile.Host=="legacy.example");
 Check(!File.Exists(Path.Combine(vault.DirectoryPath,"profile.json")));
 Check(!File.Exists(Path.Combine(vault.DirectoryPath,"known-hosts.json")));
 Check(vault.VerifyHost("legacy.example",22,"SHA256:trusted",()=>throw new Exception("Trust lost")));
 Check(!File.Exists(Path.Combine(vault.DirectoryPath,"known-hosts.json")));
 Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(vault.DirectoryPath,"known-hosts.dat"))).Contains("legacy.example"));
 return Task.CompletedTask;
});
await Test("corrupted encrypted vault: fail closed, original bytes unchanged", () => {
 var vault=new Storage(Path.Combine(root,"tampered"));vault.SaveProfile(new(){Host="test.example",User="user"});
 string path=Path.Combine(vault.DirectoryPath,"connections.dat");byte[] bytes=File.ReadAllBytes(path);bytes[^4]^=0x40;File.WriteAllBytes(path,bytes);
 try{vault.LoadConnections();throw new Exception("Tampering accepted");}catch(System.Security.Cryptography.CryptographicException){}
 Check(File.ReadAllBytes(path).SequenceEqual(bytes));return Task.CompletedTask;
});
await Test("host identity: reject, pin, mismatch, corrupt file fail closed", () => {
 Check(!storage.VerifyHost("host",22,"A",()=>false)); Check(storage.VerifyHost("host",22,"A",()=>true));
 Check(storage.VerifyHost("HOST",22,"A",()=>throw new Exception("Unexpected prompt")));
 try {storage.VerifyHost("host",22,"B",()=>true);throw new Exception("Mismatch accepted");}catch(HostTrustException){}
 var bad = new Storage(Path.Combine(root,"corrupt"));Directory.CreateDirectory(bad.DirectoryPath);File.WriteAllText(Path.Combine(bad.DirectoryPath,"known-hosts.json"),"broken");
 try {bad.VerifyHost("host",22,"A",()=>true);throw new Exception("Corrupt trust store accepted");}catch(System.Text.Json.JsonException){}
 return Task.CompletedTask;
});
var profile = new Profile{Host="127.0.0.1",User="fixture",SshPort=22222,EnableComfy=true,TargetPort=18189,LocalPort=18188,Secret="fixture-password"};
await Test("lifecycle: cancel connection, no overlapping start, no late resurrection", async () => {
 var fake = new FakeTransport(); var c = new TunnelController(()=>fake,TimeSpan.FromMilliseconds(10),_=>TimeSpan.FromMilliseconds(10));
 c.Start(profile);await fake.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
 try{c.Start(profile);throw new Exception("Concurrent start accepted");}catch(InvalidOperationException){}
 await c.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));Check(!c.IsRunning && fake.Disposed);
 await Task.Delay(50);Check(fake.ConnectCount==1);
});
await Test("lifecycle: transient failure retries, stop cancels retry wait",async()=>{
 int count=0;var retrying=new TaskCompletionSource();
 var c=new TunnelController(()=>new FailingTransport(()=>Interlocked.Increment(ref count)),retryDelay:_=>TimeSpan.FromSeconds(30));
 c.Changed+=s=>{if(s.State==TunnelState.Retrying)retrying.TrySetResult();};
 c.Start(profile);await retrying.Task.WaitAsync(TimeSpan.FromSeconds(3));await c.StopAsync();await Task.Delay(50);Check(count==1);
});

await Test("VPS switching: stop old session before starting the next, no overlap",async()=>{
 var first=new TaskCompletionSource();var second=new TaskCompletionSource();var hosts=new List<string>();int active=0,max=0;
 var c=new TunnelController(()=>new TrackingTransport(p=>{lock(hosts){hosts.Add(p.Host);active++;max=Math.Max(max,active);if(hosts.Count==1)first.TrySetResult();else second.TrySetResult();}},()=>{lock(hosts)active--;}));
 c.Start(profile with{Host="alpha.example"});await first.Task.WaitAsync(TimeSpan.FromSeconds(3));await c.StopAsync();
 c.Start(profile with{Host="beta.example"});await second.Task.WaitAsync(TimeSpan.FromSeconds(3));await c.StopAsync();
 Check(max==1 && active==0 && hosts.SequenceEqual(new[]{"alpha.example","beta.example"}));
});

if (args.Contains("--integration"))
{
 string fixture=Path.GetFullPath("tests/fixture/server.cjs");string keyPath=Path.Combine(root,"fixture-key.pem");
 var start=new ProcessStartInfo("node"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
 start.ArgumentList.Add(fixture);start.ArgumentList.Add(keyPath);
 using var server=Process.Start(start)!;
 var stderr=server.StandardError.ReadToEndAsync();
 try
 {
  Check(await server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15))=="READY","Fixture not ready");
  var trust = new Storage(Path.Combine(root,"integration"));
  SshTransport Make()=>new(trust,(_,_)=>Task.FromResult(true));
  using var http=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(15)};
  await Test("real SSH-only: no local listener, forwarding request, or Comfy HTTP probe",async()=>{
   string before=await http.GetStringAsync("http://127.0.0.1:18190/stats");
   using var busy=new TcpListener(IPAddress.Loopback,18188);busy.Start();
   using var t=Make();Check(await t.ConnectAsync(profile with{EnableComfy=false},null,default)==null);
   Check((await t.ProbeAsync(default)).Contains("转发未启用"));
   Check((await t.ProbeAsync(default)).Contains("SSH 已连接"));
   Check(before==await http.GetStringAsync("http://127.0.0.1:18190/stats"));
  });
  await Test("real SSH-only: explicit disconnect reconnects without publishing a URL",async()=>{
   var initial=new TaskCompletionSource();var restored=new TaskCompletionSource();bool dropped=false;
   var c=new TunnelController(Make,TimeSpan.FromMilliseconds(100),_=>TimeSpan.FromMilliseconds(100));
   c.Changed+=s=>{if(s.State==TunnelState.Retrying)dropped=true;if(s.State==TunnelState.Connected){Check(s.Url==null);initial.TrySetResult();if(dropped)restored.TrySetResult();}};
   c.Start(profile with{EnableComfy=false});
   try{await initial.Task.WaitAsync(TimeSpan.FromSeconds(10));await http.GetStringAsync("http://127.0.0.1:18190/drop");await restored.Task.WaitAsync(TimeSpan.FromSeconds(20));}
   finally{await c.StopAsync();}
  });
  await Test("real SSH: HTTP, 8 MiB download, 2 MiB upload, WebSocket",async()=>{
   using var transport=Make();int? port=await transport.ConnectAsync(profile,null,default);
   Check(port==18188);Check((await http.GetStringAsync($"http://127.0.0.1:{port}/"))=="fixture target");
   byte[] download=await http.GetByteArrayAsync($"http://127.0.0.1:{port}/large");Check(download.Length==8*1024*1024 && download.All(b=>b==0x5a));
   using var content=new ByteArrayContent(new byte[2*1024*1024]);using var upload=await http.PostAsync($"http://127.0.0.1:{port}/upload",content);Check(await upload.Content.ReadAsStringAsync()==(2*1024*1024).ToString());
   using var ws=new ClientWebSocket();ws.Options.Proxy=null;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
   await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"),timeout.Token);byte[] message=Encoding.UTF8.GetBytes("progress-test");await ws.SendAsync(message,WebSocketMessageType.Text,true,timeout.Token);byte[] reply=new byte[128];var result=await ws.ReceiveAsync(reply,timeout.Token);Check(reply.AsSpan(0,result.Count).SequenceEqual(message));
  });
  await Test("real SSH: encrypted private key authentication",async()=>{
   using var t=Make();await t.ConnectAsync(profile with {UseKey=true,KeyPath=keyPath,Secret="fixture-passphrase"},null,default);Check((await t.ProbeAsync(default)).Contains("200"));
  });
  await Test("real SSH: wrong password fails permanently",async()=>{
   using var t=Make();try{await t.ConnectAsync(profile with{Secret="wrong"},null,default);throw new Exception("Wrong password accepted");}catch(PermanentConnectionException){}
  });
  await Test("real SSH: occupied port auto allocation and stable-port failure",async()=>{
   using var busy=new TcpListener(IPAddress.Loopback,18188);busy.Start();
   using(var t=Make()){int? port=await t.ConnectAsync(profile,null,default);Check(port!=18188);}
   using(var t=Make()){try{await t.ConnectAsync(profile,18188,default);throw new Exception("Reconnection silently changed port");}catch(PermanentConnectionException){}}
  });
  await Test("real SSH: target down does not destroy healthy tunnel",async()=>{
   using var t=Make();await t.ConnectAsync(profile,null,default);await http.GetStringAsync("http://127.0.0.1:18190/target-off");
   Check((await t.ProbeAsync(default)).Contains("目标暂不可达"));await http.GetStringAsync("http://127.0.0.1:18190/target-on");await Task.Delay(150);Check((await t.ProbeAsync(default)).Contains("200"));
  });
  await Test("real SSH: dropped transport reconnects on same port, explicit stop releases port",async()=>{
   var connected=new TaskCompletionSource<int>();var restored=new TaskCompletionSource<int>();int seen=0;bool dropped=false;
   var c=new TunnelController(Make,TimeSpan.FromMilliseconds(100),_=>TimeSpan.FromMilliseconds(100));
   c.Changed+=s=>{if(s.State==TunnelState.Retrying)dropped=true;if(s.State==TunnelState.Connected && s.LocalPort!=null){seen++;connected.TrySetResult(s.LocalPort.Value);if(dropped)restored.TrySetResult(s.LocalPort.Value);}};
   c.Start(profile);int initial=await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
   await http.GetStringAsync("http://127.0.0.1:18190/drop");Check(await restored.Task.WaitAsync(TimeSpan.FromSeconds(25))==initial);await c.StopAsync();
   using var portCheck=new TcpListener(IPAddress.Loopback,initial);portCheck.Start();Check(!c.IsRunning);
  });
  await Test("real SSH: blackholed TCP detected by channel-open timeout",async()=>{
   using var t=Make();await t.ConnectAsync(profile,null,default);await http.GetStringAsync("http://127.0.0.1:18190/blackhole");
   bool detected=false;var clock=Stopwatch.StartNew();
   while(clock.Elapsed<TimeSpan.FromSeconds(45)){try{await t.ProbeAsync(default);}catch(IOException){detected=true;break;}await Task.Delay(200);}
   await http.GetStringAsync("http://127.0.0.1:18190/restore");Check(detected,"Half-open transport was not detected");
  });
  await Test("real SSH: 100 connect/probe/dispose cycles release the listener",async()=>{
   var process=Process.GetCurrentProcess();process.Refresh();int baseline=process.HandleCount;
   for(int i=0;i<100;i++)
   {
    using(var t=Make()){Check(await t.ConnectAsync(profile,null,default)==18188);Check((await t.ProbeAsync(default)).Contains("200"));}
    using var listener=new TcpListener(IPAddress.Loopback,18188);listener.Start();
    if((i+1)%25==0) Console.WriteLine($"  cycles: {i+1}/100");
   }
   GC.Collect();GC.WaitForPendingFinalizers();await Task.Delay(500);process.Refresh();
   Console.WriteLine($"  handles before={baseline}, after={process.HandleCount}; memory={process.WorkingSet64/1024/1024} MiB");
   Check(process.HandleCount-baseline<60,"Handle count grew unexpectedly");
  });
 }
 finally {if(!server.HasExited)server.Kill(true);await server.WaitForExitAsync();var errors=await stderr;if(errors.Length>0)Console.WriteLine("Fixture diagnostics: "+errors);}
}
Console.WriteLine($"Completed {passed} tests.");
}
catch (Exception ex)
{
 Console.Error.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
 Environment.ExitCode = 1;
}

sealed class FakeTransport : ITunnelTransport
{
 public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);public bool Disposed;public int ConnectCount;
 public async Task<int?> ConnectAsync(Profile p,int? port,CancellationToken token){ConnectCount++;Entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return 18188;}
 public Task<string> ProbeAsync(CancellationToken token)=>Task.FromResult("ok");public void Dispose()=>Disposed=true;
}
sealed class FailingTransport(Action count) : ITunnelTransport
{
 public Task<int?> ConnectAsync(Profile p,int? port,CancellationToken token){count();throw new IOException();}
 public Task<string> ProbeAsync(CancellationToken token)=>throw new NotImplementedException();public void Dispose(){}
}
sealed class TrackingTransport(Action<Profile> connected,Action disposed) : ITunnelTransport
{
 public Task<int?> ConnectAsync(Profile p,int? port,CancellationToken token){connected(p);return Task.FromResult<int?>(18188);}
 public Task<string> ProbeAsync(CancellationToken token)=>Task.FromResult("ok");public void Dispose()=>disposed();
}
