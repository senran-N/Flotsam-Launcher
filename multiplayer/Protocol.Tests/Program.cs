using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Flotsam.Multiplayer;

var passed=0;
void Check(bool value,string message){if(!value)throw new Exception(message);}
async Task Case(string name,Func<Task> run){await run();Console.WriteLine("PASS "+name);passed++;}
async Task Reject<T>(Func<Task> run) where T:Exception{try{await run();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
byte[] Zip(byte[] bytes){using var output=new MemoryStream();using(var zip=new GZipStream(output,CompressionLevel.Fastest,true))zip.Write(bytes);return output.ToArray();}

await Case("captain defaults to host and crew can build without steering",()=>{
    var crew=new CrewPolicy("host","房主");crew.Join("alice","同行者");
    Check(crew.Captain=="host"&&crew.Authorize("host","navigate")==null,"default helm");
    foreach(var kind in new[]{"navigate","navigateKeys","navigateStop","speed","speedZero","mapOpen","mapClose"})Check(crew.Authorize("alice",kind)!=null,"guest took helm: "+kind);
    foreach(var kind in new[]{"place","recipeIncrease","researchStart","markerPlace","agentRename"})Check(crew.Authorize("alice",kind)==null,"crew work blocked");
    Check(crew.Authorize("stranger","place")!=null,"offline player accepted");return Task.CompletedTask;
});
await Case("captain handoff, host reclaim and disconnect fallback",()=>{
    var crew=new CrewPolicy("host","房主");crew.Join("alice","甲");crew.Join("bob","乙");
    Check(crew.Transfer("alice","alice")!=null&&crew.Transfer("host","gone")!=null,"unauthorized handoff");
    Check(crew.Transfer("host","alice")==null&&crew.Authorize("alice","navigate")==null&&crew.Authorize("host","speed")!=null,"handoff authority");
    Check(crew.Transfer("alice","bob")==null&&crew.Transfer("host","host")==null,"captain handoff or host reclaim");
    crew.Transfer("host","alice");Check(!crew.Leave("bob")&&crew.Captain=="alice","unrelated disconnect");
    Check(crew.Leave("alice")&&crew.Captain=="host"&&crew.Members().Length==1,"captain disconnect fallback");return Task.CompletedTask;
});
await Case("welcome proof binds server assigned crew identity",()=>{
    var secret=Frames.RandomToken();var proof=Frames.Sign(secret,"host","nonce","request","files:assigned");
    Check(Frames.Verify(secret,"host","nonce","request","files:assigned",proof),"bound proof rejected");
    Check(!Frames.Verify(secret,"host","nonce","request","files:host",proof),"captain identity changed");return Task.CompletedTask;
});

await Case("optional config fields retain defaults",()=>{var config=JsonCodec.Decode<NetConfig>(Encoding.UTF8.GetBytes("{\"role\":\"probe\",\"profile\":\"D:\\\\lab\"}"));Check(config.mode=="shared"&&config.port==27027&&config.address=="127.0.0.1","config defaults");return Task.CompletedTask;});
await Case("independent rules arrive in config and gate trading, stealing, sabotage and friendly fire",()=>{
    var config=JsonCodec.Decode<NetConfig>(Encoding.UTF8.GetBytes("{\"role\":\"host\",\"profile\":\"D:\\\\lab\",\"mode\":\"independent\",\"rules\":{\"stealing\":false,\"sabotage\":true,\"trading\":false,\"friendlyFire\":true}}"));
    Check(config.rules is{stealing:false,sabotage:true,trading:false,friendlyFire:true},"rules round trip");
    Check(!config.rules.Allows("shipTransfer")&&!config.rules.Allows("shipSteal")&&!config.rules.Allows("buildingMove")&&config.rules.Allows("shipAttack"),"fleet interaction rules not enforced");
    var noSabotage=JsonCodec.Decode<NetConfig>(Encoding.UTF8.GetBytes("{\"role\":\"host\",\"profile\":\"D:\\\\lab\",\"rules\":{\"sabotage\":false}}"));
    Check(!noSabotage.rules.Allows("salvage")&&!noSabotage.rules.Allows("deactivate")&&!noSabotage.rules.Allows("shipAttack")&&noSabotage.rules.Allows("place"),"sabotage policy not enforced");
    var absent=JsonCodec.Decode<NetConfig>(Encoding.UTF8.GetBytes("{\"role\":\"host\",\"profile\":\"D:\\\\lab\"}"));
    Check(absent.rules is{stealing:true,sabotage:true,trading:true,friendlyFire:false},"missing rules must keep permissive defaults");
    return Task.CompletedTask;});
await Case("world frames carry per-ship ownership, fleet telemetry and per-ship research",()=>{
    var frame=new WorldFrame{
        worldOwner="房主",
        rules=new Rules{trading=true,stealing=false,sabotage=false,friendlyFire=true},
        ships=new[]{new FleetShipState{name="Ship:peer2:同行者",ownerName="同行者",peerId="peer2",x=65f,z=-12f,mapX=120f,mapZ=80f,energy=950f,buildings=6,agents=2,researchPoints=4,research=new ResearchState{current="desalination"}}},
        buildings=new[]{new BuildingState{id=1,owner="同行者"}},
        agents=new[]{new AgentState{id=2,owner="第三艘"}}
    };
    var copy=JsonCodec.Decode<WorldFrame>(JsonCodec.Encode(frame));
    Check(copy.worldOwner=="房主"&&copy.buildings[0].owner=="同行者"&&copy.agents[0].owner=="第三艘","owner lost in frame");
    Check(copy.rules.friendlyFire&&!copy.rules.stealing&&copy.ships.Length==1&&copy.ships[0].ownerName=="同行者"&&copy.ships[0].research.current=="desalination"&&copy.ships[0].mapX==120f,"fleet ship state lost in frame");
    return Task.CompletedTask;});
await Case("authentication rejects wrong secret, role, nonce and identity",()=>{
    var secret=Frames.RandomToken();var nonce=Frames.RandomToken();var proof=Frames.Sign(secret,"client",nonce,"alice","files");
    Check(Frames.Verify(secret,"client",nonce,"alice","files",proof),"valid proof");
    foreach(var inputs in new[]{new[]{"wrong","client",nonce,"alice","files"},new[]{secret,"host",nonce,"alice","files"},new[]{secret,"client","old","alice","files"},new[]{secret,"client",nonce,"bob","files"},new[]{secret,"client",nonce,"alice","changed"}})Check(!Frames.Verify(inputs[0],inputs[1],inputs[2],inputs[3],inputs[4],proof),"proof accepted altered input");
    Check(!Frames.Verify(secret,"client",nonce,"alice","files","bad!"),"malformed signature");return Task.CompletedTask;
});
await Case("fragmented world frame round trip",async()=>{
    using var output=new MemoryStream();await Frames.Write(output,new Packet{type="world",world=new WorldFrame{sequence=7,buildings=new[]{new BuildingState{id=1,name="同行者",inventory=new[]{new ItemCount{properties="Wood",count=3}}}}}},CancellationToken.None);
    using var fragmented=new FragmentedStream(output.ToArray());var result=await Frames.Read(fragmented,CancellationToken.None);Check(result.world!.buildings[0].inventory[0].count==3&&result.world.sequence==7,"world data");
});
await Case("reject zero, negative and oversized frames",async()=>{foreach(var prefix in new[]{new byte[4],new byte[]{255,255,255,255},new byte[]{0,128,0,1}}){using var stream=new MemoryStream(prefix);await Reject<InvalidDataException>(async()=>{await Frames.Read(stream,CancellationToken.None);});}});
await Case("reject truncated frame",async()=>{using var stream=new MemoryStream(new byte[]{0,0,0,10,123});await Reject<EndOfStreamException>(async()=>{await Frames.Read(stream,CancellationToken.None);});});
await Case("reject null command text",async()=>{using var stream=new MemoryStream();await Frames.Write(stream,new Packet{type="command",command=new GameCommand{text=null!}},CancellationToken.None);stream.Position=0;await Reject<InvalidDataException>(async()=>{await Frames.Read(stream,CancellationToken.None);});});
await Case("reject malformed world generation in command",async()=>{foreach(var epoch in new[]{null,new string('x',65)}){using var stream=new MemoryStream();await Frames.Write(stream,new Packet{type="command",command=new GameCommand{epoch=epoch!}},CancellationToken.None);stream.Position=0;await Reject<InvalidDataException>(async()=>{await Frames.Read(stream,CancellationToken.None);});}});
await Case("economy, drifters, salvage and clock retain identities in fragmented transport",async()=>{
    var epoch=Guid.NewGuid().ToString("N");var task=9007199254740993L;
    var frame=new WorldFrame{epoch=epoch,days=3,dayTime=50.25f,buildings=new[]{new BuildingState{id=91,stored=true,neighbours=new long[]{1,90},producer=new ProducerState{selected=2,threshold=-1,recipes=new[]{new RecipeState{index=2,amount=-1,properties="DryWood"}},queue=new[]{new QueueState{id=task,recipe=2,stage=3,progress=4.25f}}}}},agents=new[]{new AgentState{id=100,descriptorId=12,hunger=2,thirst=1,assignments=new[]{new AssignmentState{type=5,priority=1,enabled=true}}}},research=new ResearchState{current="node",progresses=new[]{new ResearchProgress{guid="node",progress=3}}},markers=new[]{new MarkerState{id=80,properties="swimming",limit=3,disabled=new[]{"Plastic"}}},flotsam=new[]{new FlotsamState{id=81,properties="wood",items=new[]{new ItemCount{properties="WetWood",count=2}}}}};
    using var output=new MemoryStream();await Frames.Write(output,new Packet{type="world",world=frame},CancellationToken.None);using var input=new FragmentedStream(output.ToArray());var value=(await Frames.Read(input,CancellationToken.None)).world!;
    Check(value.buildings[0].stored,"stored membership lost in transport");Check(value.epoch==epoch&&value.buildings[0].producer!.queue[0].id==task&&value.buildings[0].producer!.threshold==-1,"stable queue identity");Check(value.research.progresses[0].progress==3&&value.agents[0].descriptorId==12&&value.markers[0].disabled[0]=="Plastic"&&value.flotsam[0].items[0].count==2&&value.dayTime==50.25f,"replicated economy payload");
});
await Case("command retries never double spend",()=>{var ledger=new CommandLedger();var effects=0;var command=new GameCommand{sequence=1};var first=ledger.Execute("alice",command,_=>{effects++;return null;});var retry=ledger.Execute("alice",command,_=>{effects++;return null;});Check(first.accepted&&ReferenceEquals(first,retry)&&effects==1,"duplicate execution");Check(!ledger.Execute("alice",new GameCommand{sequence=0},_=>null).accepted,"zero sequence");Check(ledger.Execute("bob",command,_=>null).accepted,"per peer sequences");return Task.CompletedTask;});
await Case("closed peer high-water marks can be released without losing retry history",()=>{var ledger=new CommandLedger();var effects=0;var command=new GameCommand{sequence=1};var first=ledger.Execute("closed",command,_=>{effects++;return null;});ledger.Forget("closed");Check(ReferenceEquals(first,ledger.Execute("closed",command,_=>{effects++;return null;}))&&effects==1,"retained replay history");Check(ledger.Execute("new-assigned-peer",new GameCommand{sequence=1},_=>null).accepted,"new identity rejected");return Task.CompletedTask;});
await Case("evicted command remains stale",()=>{var ledger=new CommandLedger();for(var i=1;i<=4100;i++)ledger.Execute("alice",new GameCommand{sequence=i},_=>null);Check(!ledger.Execute("alice",new GameCommand{sequence=1},_=>throw new Exception("replayed")).accepted,"stale replay");return Task.CompletedTask;});
await Case("snapshot accepts gzip JSON and rejects binary",()=>{SnapshotGuard.Validate(Zip(Encoding.UTF8.GetBytes("{\"world\":1}")));try{SnapshotGuard.Validate(Zip(new byte[]{0,1,0,0}));throw new Exception("binary accepted");}catch(InvalidDataException){}return Task.CompletedTask;});
await Case("snapshot rejects decompression bomb",()=>{var bytes=new byte[33*1024*1024];bytes[0]=(byte)'{';try{SnapshotGuard.Validate(Zip(bytes));throw new Exception("bomb accepted");}catch(InvalidDataException){}return Task.CompletedTask;});
await Case("real TCP connections exchange command and acknowledge then shut down",async()=>{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    using var outgoing=new TcpClient();await outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint);using var incoming=await listener.AcceptTcpClientAsync();listener.Stop();
    using var host=new Connection(incoming,"client");using var client=new Connection(outgoing,"host");var reply=new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
    var hostTask=host.Run((connection,packet)=>{Check(packet.command?.sequence==42,"command");connection.Send(new Packet{type="result",result=new CommandResult{sequence=42,accepted=true}});});
    var clientTask=client.Run((_,packet)=>reply.TrySetResult(packet));client.Send(new Packet{type="command",command=new GameCommand{sequence=42,kind="rename",text="共享世界"}});
    var result=await reply.Task.WaitAsync(TimeSpan.FromSeconds(5));Check(result.result?.accepted==true,"ack");client.Dispose();await Task.WhenAll(clientTask,hostTask).WaitAsync(TimeSpan.FromSeconds(5));Check(!host.Open&&!client.Open,"closed sockets");
});
await Case("slow peer bounded queue disconnects",()=>{using var socket=new TcpClient();using var connection=new Connection(socket,"slow");for(var i=0;i<17;i++)connection.Send(new Packet{type="ping"});Check(!connection.Open,"unbounded queue");return Task.CompletedTask;});
await Case("obsolete world frames coalesce while reliable replies remain queued",async()=>{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();using var outgoing=new TcpClient();await outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint);using var incoming=await listener.AcceptTcpClientAsync();listener.Stop();
    using var host=new Connection(incoming,"client");for(var i=1;i<=10000;i++)host.Send(new Packet{type="world",world=new WorldFrame{sequence=i}});
    host.Send(new Packet{type="result",result=new CommandResult{sequence=9,accepted=true}});Check(host.Open&&host.PendingPackets==2,"world queue grew or lost reliable reply");
    var running=host.Run((_,_)=>{});using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var world=await Frames.Read(outgoing.GetStream(),deadline.Token);var result=await Frames.Read(outgoing.GetStream(),deadline.Token);
    Check(world.world?.sequence==10000&&result.result?.sequence==9,"coalesced frame or reply lost");host.Dispose();await running.WaitAsync(TimeSpan.FromSeconds(5));
});
await Case("broadcast packet encoding is shared across peers",()=>{var packet=new Packet{type="world",world=new WorldFrame{sequence=42}};Check(ReferenceEquals(packet.Encode(),packet.Encode()),"broadcast repeatedly serialized");return Task.CompletedTask;});
Console.WriteLine($"{passed} protocol tests passed");
var loadPassed=FleetLoadTests.Run();
Console.WriteLine($"{passed+loadPassed} protocol and 8-peer transport tests passed");

sealed class FragmentedStream(byte[] bytes):MemoryStream(bytes)
{
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>base.ReadAsync(buffer,offset,Math.Min(3,count),token);
}
