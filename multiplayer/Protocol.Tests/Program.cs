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

await Case("world deltas reconstruct changes, removals, skipped captures and fresh epochs",()=>{
    var encoder=new WorldDeltaEncoder();var decoder=new WorldDeltaDecoder();
    var first=new WorldFrame{epoch="a",sequence=1,buildings=new[]{new BuildingState{id=1,name="one"},new BuildingState{id=2,name="two"}}};
    Check(decoder.Apply(encoder.Encode(first)).buildings.Length==2,"initial full frame");
    var third=new WorldFrame{epoch="a",sequence=3,buildings=new[]{new BuildingState{id=2,name="changed"},new BuildingState{id=3,name="three"}}};
    var delta=encoder.Encode(third);Check(delta.delta&&delta.basis==1&&delta.removedBuildings.SequenceEqual(new long[]{1}),"baseline follows written packets");
    var restored=decoder.Apply(JsonCodec.Decode<WorldFrame>(JsonCodec.Encode(delta)));
    Check(restored.buildings.Length==2&&restored.buildings.Single(b=>b.id==2).name=="changed","delta did not reconstruct");
    third=new WorldFrame{epoch="a",sequence=4,buildings=third.buildings};delta=encoder.Encode(third);
    Check(delta.buildings.Length==0&&decoder.Apply(delta).buildings.Length==2,"unchanged buildings resent or removed");
    try{new WorldDeltaDecoder().Apply(delta);throw new Exception("missing basis accepted");}catch(InvalidDataException){}
    Check(!encoder.Encode(new WorldFrame{epoch="b",sequence=5}).delta,"new world reused old baseline");
    return Task.CompletedTask;
});
await Case("bootstrap chunks preserve complete snapshots and reject missing or reordered data",()=>{
    var bytes=new byte[220000];Random.Shared.NextBytes(bytes);
    var packet=new Packet{type="bootstrap",ship="ship",world=new WorldFrame{epoch="a"},bootstrap=Convert.ToBase64String(bytes)};
    var chunks=BootstrapTransfer.Split(packet).ToArray();var transfer=new BootstrapTransfer();Packet? result=null;
    foreach(var chunk in chunks)result=transfer.Receive(JsonCodec.Decode<Packet>(JsonCodec.Encode(chunk)));
    Check(result?.ship=="ship"&&Convert.FromBase64String(result.bootstrap).SequenceEqual(bytes),"incomplete bootstrap");
    transfer=new BootstrapTransfer();transfer.Receive(chunks[0]);
    try{transfer.Receive(chunks[2]);throw new Exception("out of order accepted");}catch(InvalidDataException){}
    return Task.CompletedTask;
});

await Case("alliance needs an unexpired request addressed to the authenticated recipient",()=>{
    var requests=new DiplomacyRequests();
    Check(requests.Respond(1,"B","A",0)!=null,"unsolicited acceptance");
    Check(requests.Request("A","B","Alice",0)==null,"request rejected");
    var id=requests.Capture(1).Single().id;
    Check(requests.Respond(id,"A","B",1)!=null,"sender accepted own request");
    Check(requests.Request("B","A","Bob",1)==null&&requests.Capture(1).Length==2,"cross invitations auto accepted");
    Check(requests.Respond(id,"B","A",1)==null&&requests.Respond(id,"B","A",1)!=null,"duplicate acceptance");
    var expired=requests.Capture(2).Single().id;
    Check(requests.Respond(expired,"A","B",92)!=null,"expired invitation accepted");
    return Task.CompletedTask;
});
await Case("manual ballistics obey gravity and limit firing angles",()=>{
    Check(Ballistics.ValidAim(0,15)&&!Ballistics.ValidAim(121,15)&&!Ballistics.ValidAim(0,float.NaN),"invalid aim accepted");
    var h=Ballistics.FlightHeight(3,20,1);
    Check(Math.Abs(h-15)<.001f&&Ballistics.FlightHeight(3,20,4)<0,"trajectory ignores gravity");
    return Task.CompletedTask;
});
await Case("tactical aiming reaches the cursor from the actual muzzle and rejects unreachable sea points",()=>{
    foreach(var height in new[]{.5f,1f,3f,8f})
    {
        Ballistics.RangeLimits(height,out var minimum,out var maximum);
        Check(minimum>Ballistics.MuzzleOffset&&maximum>150,"firing sector lost its blind zone or reach");
        Check(!Ballistics.TrySolve(minimum-2,height,0,out _)&&!Ballistics.TrySolve(maximum+2,height,0,out _),"unreachable water point accepted");
        for(var distance=minimum+1;distance<maximum-1;distance+=3)
        {
            Check(Ballistics.TrySolve(distance,height,0,out var pitch)&&Ballistics.ValidAim(0,pitch),"reachable sea point rejected");
            var a=pitch*MathF.PI/180;var cos=MathF.Cos(a);var sin=MathF.Sin(a);
            var time=(distance-Ballistics.MuzzleOffset*cos)/(Ballistics.MuzzleSpeed*cos);
            var impact=height+Ballistics.MuzzleOffset*sin+Ballistics.MuzzleSpeed*sin*time-.5f*Ballistics.Gravity*time*time;
            Check(MathF.Abs(impact)<.002f,"cursor solver disagrees with the real shot's muzzle displacement");
        }
    }
    Check(!Ballistics.TrySolve(float.NaN,1,0,out _)&&!Ballistics.TrySolve(float.PositiveInfinity,1,0,out _),"non-finite target accepted");
    Check(Ballistics.TrySolve(80,1,12,out var elevated),"raised target cannot be aimed at");
    var angle=elevated*MathF.PI/180;var flight=(80-Ballistics.MuzzleOffset*MathF.Cos(angle))/(Ballistics.MuzzleSpeed*MathF.Cos(angle));
    Check(MathF.Abs(Ballistics.FlightHeight(1+Ballistics.MuzzleOffset*MathF.Sin(angle),Ballistics.MuzzleSpeed*MathF.Sin(angle),flight)-12)<.002f,"raised trajectory misses requested height");
    return Task.CompletedTask;
});
await Case("persistent player key signs the current challenge and cannot be replaced by a nickname",()=>{
    var folder=Path.Combine(Path.GetTempPath(),"flotsam-identity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
    try{
        var path=Path.Combine(folder,"identity.key");string publicKey,owner;
        using(var key=new PlayerIdentity(path)){
            publicKey=key.PublicKey;var proof=key.Sign("nonce","peer","files","ticket");
            owner=PlayerIdentity.Verify(publicKey,proof,"nonce","peer","files","ticket");
            try{PlayerIdentity.Verify(publicKey,proof,"different","peer","files","ticket");throw new Exception("replay accepted");}catch(InvalidDataException){}
        }
        using(var key=new PlayerIdentity(path))Check(key.PublicKey==publicKey&&PlayerIdentity.Verify(key.PublicKey,key.Sign("next","peer2","files","ticket2"),"next","peer2","files","ticket2")==owner,"owner changed on restart");
    }finally{Directory.Delete(folder,true);}
    return Task.CompletedTask;
});

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
await Case("live connection reconstructs chunked bootstrap and successive deltas before callbacks",async()=>{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    using var outgoing=new TcpClient();await outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint);using var incoming=await listener.AcceptTcpClientAsync();listener.Stop();
    using var host=new Connection(incoming,"client");using var client=new Connection(outgoing,"host");
    var received=System.Threading.Channels.Channel.CreateUnbounded<Packet>();
    var tasks=new[]{host.Run((_,_)=>{}),client.Run((_,p)=>received.Writer.TryWrite(p))};
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var bytes=new byte[180000];Random.Shared.NextBytes(bytes);var snapshot=Convert.ToBase64String(bytes);
    host.Send(new Packet{type="bootstrap",bootstrap=snapshot,ship="own",world=new WorldFrame{epoch="e",sequence=1}});
    var packet=await received.Reader.ReadAsync(deadline.Token);Check(packet.type=="bootstrap"&&packet.bootstrap==snapshot&&packet.ship=="own","callback saw partial bootstrap");
    host.Send(new Packet{type="world",world=new WorldFrame{epoch="e",sequence=2,buildings=new[]{new BuildingState{id=1,name="keep"},new BuildingState{id=2,name="remove"}}}});
    packet=await received.Reader.ReadAsync(deadline.Token);Check(packet.world?.buildings.Length==2,"first frame lost");
    host.Send(new Packet{type="world",world=new WorldFrame{epoch="e",sequence=4,buildings=new[]{new BuildingState{id=1,name="keep"},new BuildingState{id=3,name="new"}}}});
    packet=await received.Reader.ReadAsync(deadline.Token);Check(packet.world?.buildings.Length==2&&packet.world.buildings.Any(b=>b.id==1)&&packet.world.buildings.Any(b=>b.id==3)&&packet.world.buildings.All(b=>b.id!=2),"callback received raw delta");
    client.Dispose();host.Dispose();await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
});
await Case("obsolete world frames coalesce while reliable replies remain queued",async()=>{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();using var outgoing=new TcpClient();await outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint);using var incoming=await listener.AcceptTcpClientAsync();listener.Stop();
    using var host=new Connection(incoming,"client");for(var i=1;i<=10000;i++)host.Send(new Packet{type="world",world=new WorldFrame{sequence=i}});
    host.Send(new Packet{type="result",result=new CommandResult{sequence=9,accepted=true}});Check(host.Open&&host.PendingPackets==2,"world queue grew or lost reliable reply");
    var running=host.Run((_,_)=>{});using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));var world=await Frames.Read(outgoing.GetStream(),deadline.Token);var result=await Frames.Read(outgoing.GetStream(),deadline.Token);
    Check(world.world?.sequence==10000&&result.result?.sequence==9,"coalesced frame or reply lost");host.Dispose();await running.WaitAsync(TimeSpan.FromSeconds(5));
});
await Case("bootstrap packet replaces obsolete world frame in queue during resync",async()=>{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();using var outgoing=new TcpClient();await outgoing.ConnectAsync((IPEndPoint)listener.LocalEndpoint);using var incoming=await listener.AcceptTcpClientAsync();listener.Stop();
    using var host=new Connection(incoming,"client");
    for(var i=1;i<=10;i++)host.Send(new Packet{type="world",world=new WorldFrame{sequence=i}});
    Check(host.Open&&host.PendingPackets==1,"coalesced world frame queued");
    var snapshot=Convert.ToBase64String(Encoding.UTF8.GetBytes("resync-snapshot"));
    host.Send(new Packet{type="bootstrap",bootstrap=snapshot,world=new WorldFrame{sequence=99}});
    Check(host.Open&&host.PendingPackets==1,"bootstrap must replace world frame in-place without growing queue");
    var running=host.Run((_,_)=>{});using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var transfer=new BootstrapTransfer();Packet? packet=null;
    while(packet==null)packet=transfer.Receive(await Frames.Read(outgoing.GetStream(),deadline.Token));
    Check(packet.type=="bootstrap"&&packet.bootstrap==snapshot&&packet.world?.sequence==99,"client must receive newest bootstrap instead of obsolete world frame");
    host.Dispose();await running.WaitAsync(TimeSpan.FromSeconds(5));
});
await Case("broadcast packet encoding is shared across peers",()=>{var packet=new Packet{type="world",world=new WorldFrame{sequence=42}};Check(ReferenceEquals(packet.Encode(),packet.Encode()),"broadcast repeatedly serialized");return Task.CompletedTask;});
await Case("span interpolation eliminates high-speed sawtooth velocity jitter across 1x, 2x and 4x speeds",()=>{
    foreach(var speedScale in new[]{1f,2f,4f})
    {
        var clock=new SnapshotSpanClock();
        var agents=new PoseTrackMap();
        var markers=new PoseTrackMap();
        var flotsam=new PoseTrackMap();
        const float snapshotDt=0.05f;
        const float renderDt=0.01f;
        var hostAgentVel=3.5f*speedScale;
        var hostSailVel=-12f*speedScale;
        var agentVisual=new PoseSample(0f,0f,0f,0f);
        var markerVisual=new PoseSample(15f,0f,30f,350f);
        var flotsamVisual=new PoseSample(17f,0f,30f,20f);
        var steadySpeeds=new List<float>();
        for(var snap=0;snap<=12;snap++)
        {
            var t=snap*snapshotDt;
            clock.BeginFrame(t);
            var waveY=MathF.Sin(t*speedScale*2.5f)*0.35f;
            var agentTarget=new PoseSample(hostAgentVel*t,0f,0f,90f);
            var markerTarget=new PoseSample(15f,waveY,30f+hostSailVel*t,(350f+snap*4f*speedScale)%360f);
            var flotsamTarget=new PoseSample(17f,waveY,30f+hostSailVel*t,20f);
            if(agents.UpdateTarget(1,agentVisual,agentTarget,400f))agentVisual=agentTarget;
            if(markers.UpdateTarget(2,markerVisual,markerTarget,400f))markerVisual=markerTarget;
            if(flotsam.UpdateTarget(3,flotsamVisual,flotsamTarget,400f))flotsamVisual=flotsamTarget;
            if(snap==12)break;
            for(var step=0;step<5;step++)
            {
                var alpha=clock.Advance(renderDt);
                var prevAgentX=agentVisual.X;
                Check(agents.Sample(1,alpha,out agentVisual),"agent sample");
                Check(markers.Sample(2,alpha,out markerVisual),"marker sample");
                Check(flotsam.Sample(3,alpha,out flotsamVisual),"flotsam sample");
                var spacingX=flotsamVisual.X-markerVisual.X;
                var spacingZ=flotsamVisual.Z-markerVisual.Z;
                Check(MathF.Abs(spacingX-2f)<0.001f&&MathF.Abs(spacingZ)<0.001f,"marker and flotsam sheared during sailing");
                if(snap>=4)steadySpeeds.Add((agentVisual.X-prevAgentX)/renderDt);
            }
        }
        var minVel=steadySpeeds.Min();
        var maxVel=steadySpeeds.Max();
        Check(maxVel/minVel<1.02f,$"velocity sawtooth persisted at {speedScale}x: min={minVel} max={maxVel}");
        Check(MathF.Abs(steadySpeeds.Average()-hostAgentVel)<0.05f*hostAgentVel,"steady-state speed mismatch");
    }
    var wrap=PoseSample.Lerp(new PoseSample(0f,0f,0f,355f),new PoseSample(0f,0f,0f,5f),0.5f);
    Check(wrap.Yaw<1f||wrap.Yaw>359f,"shortest-arc yaw wraparound failed: "+wrap.Yaw);
    var teleportMap=new PoseTrackMap();
    Check(teleportMap.UpdateTarget(9,new PoseSample(0f,0f,0f,0f),new PoseSample(1f,0f,0f,0f),400f),"initial spawn must snap");
    Check(!teleportMap.UpdateTarget(9,new PoseSample(1f,0f,0f,0f),new PoseSample(3f,0f,0f,0f),400f),"normal step must interpolate");
    Check(teleportMap.UpdateTarget(9,new PoseSample(3f,0f,0f,0f),new PoseSample(30f,0f,0f,0f),400f),"27m jump must snap");
    return Task.CompletedTask;
});
await Case("world frame preserves agent terrain, boarded boat id and marker rotation",()=>{
    var frame=new WorldFrame{
        agents=new[]{new AgentState{id=7,terrain=0,boatId=42,activity=3,rotation=135f}},
        markers=new[]{new MarkerState{id=11,x=12f,y=-0.25f,z=34f,rotation=272.5f,radius=1,limit=2}}
    };
    var decoded=JsonCodec.Decode<WorldFrame>(JsonCodec.Encode(frame));
    Check(decoded.agents[0].terrain==0&&decoded.agents[0].boatId==42&&MathF.Abs(decoded.markers[0].rotation-272.5f)<0.001f&&MathF.Abs(decoded.markers[0].y+0.25f)<0.001f,"agent/marker pose metadata lost");
    return Task.CompletedTask;
});
await Case("planned navigation, sea beacons, captain requests and road store policies",async()=>{
    var crew=new CrewPolicy("host","房主");crew.Join("alice","甲");
    Check(crew.Authorize("alice","navigatePlan")!=null&&crew.Authorize("host","navigatePlan")==null,"navigatePlan must require captain");
    Check(crew.Authorize("alice","seaPing")==null&&crew.Authorize("alice","captainRequest")==null,"crew co-op signals must be permitted");
    Check(crew.Authorize("stranger","seaPing")!=null&&crew.Authorize("stranger","captainRequest")!=null,"offline peer must be rejected");
    var lockedRules=new Rules{stealing=false,sabotage=false,trading=true,friendlyFire=false};
    Check(!lockedRules.Allows("roadStore")&&!new Rules{stealing=true}.Allows("roadStore"),"raiding must never authorize foreign road management");
    var waypoints=string.Join(";",Enumerable.Range(1,10).Select(i=>$"{i*35.5:0.0},{-i*18.2:0.0}"));
    using(var okStream=new MemoryStream())
    {
        await Frames.Write(okStream,new Packet{type="command",command=new GameCommand{epoch="e",kind="navigatePlan",x=355f,z=-182f,text=waypoints}},CancellationToken.None);
        okStream.Position=0;
        var read=(await Frames.Read(okStream,CancellationToken.None)).command!;
        Check(read.kind=="navigatePlan"&&read.text==waypoints,"multi-waypoint navigatePlan round trip");
    }
    using(var overStream=new MemoryStream())
    {
        await Frames.Write(overStream,new Packet{type="command",command=new GameCommand{epoch="e",kind="navigatePlan",text=new string('w',513)}},CancellationToken.None);
        overStream.Position=0;
        await Reject<InvalidDataException>(async()=>{await Frames.Read(overStream,CancellationToken.None);});
    }
    var actionFrame=new WorldFrame{
        actions=new[]{
            new FleetActionState{id=5,kind="shipTransfer",label="同行者 · 运送 木材 ×8",sourceShip="Ship:p2:同行者",targetShip="host",targetBuilding=77},
            new FleetActionState{id=6,kind="seaPing",label="甲 · 海域信标",startX=120f,startZ=-45f,sourceShip="p2"}
        }
    };
    var copy=JsonCodec.Decode<WorldFrame>(JsonCodec.Encode(actionFrame));
    Check(copy.actions.Length==2&&copy.actions[0].sourceShip=="Ship:p2:同行者"&&copy.actions[0].targetShip=="host"&&copy.actions[0].targetBuilding==77,"moving-ship action targets lost");
    Check(copy.actions[1].kind=="seaPing"&&MathF.Abs(copy.actions[1].startX-120f)<0.001f,"seaPing action lost");
});
await Case("stationary ocean anchor keeps sea resources fixed while ship sails and round-trips cursor commands",async()=>{
    var anchor=new OceanAnchorMath();
    anchor.UpdateHostFrame(100f,200f,30f);

    // Canonical ocean resource at (150, 280) yaw 75°
    const float resOceanX=150f,resOceanZ=280f,resOceanYaw=75f;
    anchor.OceanToClientScene(resOceanX,resOceanZ,resOceanYaw,out var initialClientResX,out var initialClientResZ,out var initialClientResYaw);

    var prevShipDistSq=float.MaxValue;
    for(var step=1;step<=20;step++)
    {
        var t=step/20f;
        var hostX=100f+(resOceanX-100f)*t;
        var hostZ=200f+(resOceanZ-200f)*t;
        var hostYaw=30f+42f*t;
        anchor.UpdateHostFrame(hostX,hostZ,hostYaw);

        // On Host's floating-origin treadmill, the stationary ocean resource moves inversely in Host scene space:
        anchor.OceanToHostScene(resOceanX,resOceanZ,resOceanYaw,out var hostResX,out var hostResZ,out var hostResYaw);

        // On Client, HostToClient must map that resource to the EXACT same stationary Client scene coordinate!
        anchor.HostToClient(hostResX,hostResZ,hostResYaw,out var clientResX,out var clientResZ,out var clientResYaw);
        var drift=MathF.Sqrt((clientResX-initialClientResX)*(clientResX-initialClientResX)+(clientResZ-initialClientResZ)*(clientResZ-initialClientResZ));
        Check(drift<0.0005f,$"sea resource drifted {drift}m on client");
        Check(MathF.Abs(PoseSample.ShortestAngleDelta(clientResYaw,initialClientResYaw))<0.01f,"sea resource yaw drifted on client");

        // Meanwhile, the Host's ship (at Host scene 0,0,0) physically sails toward the stationary resource on Client!
        anchor.HostToClient(0f,0f,0f,out var shipClientX,out var shipClientZ,out _);
        var dx=initialClientResX-shipClientX;
        var dz=initialClientResZ-shipClientZ;
        var distSq=dx*dx+dz*dz;
        Check(distSq<prevShipDistSq,"ship must visibly approach stationary sea resource on client");
        prevShipDistSq=distSq;

        // Outbound Client cursor commands (place, roadPlace, markerPlace, buildingMove) round-trip to exact Host scene coords:
        var wantedHostX=8.5f;var wantedHostZ=-6.25f;var wantedHostYaw=90f;
        anchor.HostToClient(wantedHostX,wantedHostZ,wantedHostYaw,out var clickClientX,out var clickClientZ,out var clickClientYaw);
        anchor.ClientToHost(clickClientX,clickClientZ,clickClientYaw,out var rtHostX,out var rtHostZ,out var rtHostYaw);
        Check(MathF.Abs(rtHostX-wantedHostX)<0.0005f&&MathF.Abs(rtHostZ-wantedHostZ)<0.0005f,"ClientToHost position mismatch");
        Check(MathF.Abs(PoseSample.ShortestAngleDelta(rtHostYaw,wantedHostYaw))<0.01f,"ClientToHost yaw mismatch");
    }
    Check(prevShipDistSq<0.0001f,"ship must reach the stationary resource at step 20");

    // Verify >750m precision rebase preserves exact canonical ocean coordinates across PoseTrackMap.ShiftAll
    anchor.UpdateHostFrame(1200f,-900f,115f);
    anchor.HostToClient(0f,0f,0f,out var farClientX,out var farClientZ,out var farClientYaw);
    Check(anchor.ShouldRebase(farClientX,farClientZ),"ShouldRebase must trigger past 750m");
    var tracks=new PoseTrackMap();
    var prePose=new PoseSample(farClientX,0f,farClientZ,farClientYaw);
    tracks.UpdateTarget(1L,prePose,prePose,625f);
    anchor.ClientSceneToOcean(prePose.X,prePose.Z,prePose.Yaw,out var preOx,out var preOz,out var preOyaw);
    anchor.RebaseToClientScenePoint(farClientX,farClientZ,out var oldAx,out var oldAz,out var oldAyaw);
    tracks.ShiftAll(s=>OceanAnchorMath.RebasePose(oldAx,oldAz,oldAyaw,anchor.AnchorX,anchor.AnchorZ,anchor.AnchorYaw,s));
    Check(tracks.Sample(1L,0.5f,out var postPose)&&MathF.Abs(postPose.X)<0.001f&&MathF.Abs(postPose.Z)<0.001f,"rebased local ship must be centered near client origin");
    anchor.ClientSceneToOcean(postPose.X,postPose.Z,postPose.Yaw,out var postOx,out var postOz,out var postOyaw);
    Check(MathF.Abs(postOx-preOx)<0.001f&&MathF.Abs(postOz-preOz)<0.001f&&MathF.Abs(PoseSample.ShortestAngleDelta(postOyaw,preOyaw))<0.01f,"rebase altered canonical ocean pose");

    // Verify LandmarkState & island scavenging / drifter rescue command permissions
    var crew=new CrewPolicy("host","房主");crew.Join("bob","乙");
    foreach(var kind in new[]{"landmarkActivate","landmarkDeactivate","landmarkLimit","landmarkBoat","landmarkItemFilter","landmarkCatToggle","landmarkRescueToggle","landmarkDirectRescue"})
        Check(crew.Authorize("bob",kind)==null,$"crew must be allowed to execute {kind}");
    var lmFrame=new WorldFrame{
        landmarks=new[]{
            new LandmarkState{
                mapX=-340.5f,mapZ=512.25f,
                actions=new[]{
                    new LandmarkActionState{index=0,state=2,limit=2,useBoat=true,owner="Ship:p2:乙",disabledCategories=new[]{1},disabledItems=new[]{"WetWood"},toggledRescueables=new[]{0}}
                }
            }
        }
    };
    using var ms=new MemoryStream();
    await Frames.Write(ms,new Packet{type="world",world=lmFrame},CancellationToken.None);
    ms.Position=0;
    var decodedLm=(await Frames.Read(ms,CancellationToken.None)).world!;
    Check(decodedLm.landmarks.Length==1&&MathF.Abs(decodedLm.landmarks[0].mapX+340.5f)<0.001f&&decodedLm.landmarks[0].actions[0].useBoat&&decodedLm.landmarks[0].actions[0].toggledRescueables[0]==0,"landmark state wire round trip");
});
await Case("mid-late game decorations, poles, specialists, birds and biome transitions round-trip cleanly",async()=>{
    var crew=new CrewPolicy("host","房主");crew.Join("alice","甲");
    Check(crew.Authorize("alice","nextBiome")!=null&&crew.Authorize("host","nextBiome")==null,"nextBiome must require captain");
    foreach(var kind in new[]{"nextBiomeReady","buildingRepair","shipFlare","shipAllyRequest","shipAllyAccept","shipAllyBreak","shipFloatBarrel","shipBarterOffer","shipBarterAccept","shipBarterCancel","radioSelect","dialogueChoice","decoPlace","energyConnect","storageTag","aquaActive","distAmount","ozoneTrigger","agentDiet","agentLevelAttr","birdRename","birdHouseFood","moveBoat"})
        Check(crew.Authorize("alice",kind)==null,$"crew must be allowed to submit {kind}");

    var strictRules=new Rules{stealing=false,sabotage=false,trading=true,friendlyFire=false};
    Check(!strictRules.Allows("moveBoat")&&!new Rules{stealing=true}.Allows("moveBoat"),"raiding cannot move a foreign boat");
    Check(!strictRules.Allows("decoRemove")&&!strictRules.Allows("birdFree")&&!strictRules.Allows("storageTag")&&!new Rules{sabotage=true}.Allows("decoRemove"),"sabotage cannot manage foreign buildings or birds");

    var fullFrame=new WorldFrame{
        activeDialogue="RadioTower_Distress_01 [可选择]",
        ships=new[]{new FleetShipState{name="Ship:p2:甲",ownerName="甲",peerId="p2",birds=3,buildings=9,agents=4}},
        birds=new[]{
            new BirdReplicaState{id=301,birdHouseId=55,owner="Ship:p2:甲",name="阿鸥",descriptor="Seagull_01",descriptorId=2,state=4,happiness=85,isFed=true,carriedItem="Plastic",x=-14.5f,y=6.25f,z=22.75f,rotation=210f}
        },
        buildings=new[]{
            new BuildingState{
                id=55,owner="Ship:p2:甲",properties="BirdHouse",
                decorations=new[]{
                    new DecorationState{properties="Crop_Tomato",name="番茄垄",turn=2,visual=1,phase=2,limit=1,progress=1f,cropProgress=0.75f,slots=new[]{0,1},inventory=new[]{new ItemCount{properties="FreshWater",count=2}}},
                    new DecorationState{properties="Fence_Wood",turn=0,visual=0,phase=2,limit=1,progress=1f,slots=new[]{0}}
                },
                poleLinks=new[]{new EnergyWireState{srcSlot=3,dstBuilding=56,dstSlot=-1}},
                storageFilter=new StorageFilterState{acceptedTags=14,disabledItems=new[]{"RottenFish"}},
                aquaFarm=new AquaFarmState{
                    activeFish="Guppy",
                    broodstock=new[]{new FishEntryState{fish="Guppy",consumed=1.5f,progress=0.4f,hungry=false}},
                    fishes=new[]{new FishEntryState{fish="Guppy",consumed=0.5f,progress=0.8f,hungry=true}}
                },
                distributers=new[]{new DistributerItemState{item="FreshWater",available=12f,threshold=5f,amount=2f}},
                birdHouse=new BirdHouseState{refillFood=true,exportItems=false,disabledGroups=new[]{2,5}}
            }
        },
        agents=new[]{
            new AgentState{
                id=101,owner="Ship:p2:甲",name="老海狗",level=4,spendablePoints=2,experience=145.5f,pollution=18.25f,disease="PollutedCough",
                attributes=new[]{new AttributeState{type=1,level=3},new AttributeState{type=4,level=2}},
                diets=new[]{new DietEntryState{vital=0,priority=2,item="GrilledFish"}}
            }
        }
    };

    using var stream=new MemoryStream();
    await Frames.Write(stream,new Packet{type="world",world=fullFrame},CancellationToken.None);
    using var frag=new FragmentedStream(stream.ToArray());
    var decoded=(await Frames.Read(frag,CancellationToken.None)).world!;

    Check(decoded.activeDialogue=="RadioTower_Distress_01 [可选择]"&&decoded.ships[0].birds==3,"dialogue or fleet bird count lost");
    Check(decoded.birds.Length==1&&decoded.birds[0].name=="阿鸥"&&decoded.birds[0].carriedItem=="Plastic"&&decoded.birds[0].isFed&&MathF.Abs(decoded.birds[0].y-6.25f)<0.001f,"bird replica state lost");
    var b=decoded.buildings[0];
    Check(b.decorations.Length==2&&MathF.Abs(b.decorations[0].cropProgress-0.75f)<0.001f&&b.decorations[1].properties=="Fence_Wood","decorations/crops lost");
    Check(b.poleLinks.Length==1&&b.poleLinks[0].srcSlot==3&&b.poleLinks[0].dstBuilding==56&&b.poleLinks[0].dstSlot==-1,"pole wire link lost");
    Check(b.storageFilter?.acceptedTags==14&&b.storageFilter.disabledItems[0]=="RottenFish","storage filter lost");
    Check(b.aquaFarm?.activeFish=="Guppy"&&b.aquaFarm.broodstock.Length==1&&b.aquaFarm.fishes[0].hungry,"aquaFarm state lost");
    Check(b.distributers.Length==1&&MathF.Abs(b.distributers[0].threshold-5f)<0.001f,"distributer state lost");
    Check(b.birdHouse is{refillFood:true,exportItems:false}&&b.birdHouse.disabledGroups.SequenceEqual(new[]{2,5}),"birdHouse state lost");
    var a=decoded.agents[0];
    Check(a.level==4&&a.spendablePoints==2&&MathF.Abs(a.experience-145.5f)<0.001f&&a.disease=="PollutedCough","agent progression/disease lost");
    Check(a.attributes.Length==2&&a.attributes[0].level==3&&a.diets.Length==1&&a.diets[0].item=="GrilledFish","agent attributes/diets lost");

    // Verify 3D airborne bird flight pose interpolation preserves altitude Y across anchor rebase
    var birdTracks=new PoseTrackMap();
    var p0=new PoseSample(760f,8.5f,-120f,45f);
    var p1=new PoseSample(764f,11.5f,-116f,55f);
    birdTracks.UpdateTarget(301L,p0,p0,900f);
    birdTracks.UpdateTarget(301L,p0,p1,900f);
    var anchor=new OceanAnchorMath();
    anchor.UpdateHostFrame(800f,-100f,30f);
    anchor.RebaseToClientScenePoint(760f,-120f,out var oldAx,out var oldAz,out var oldAyaw);
    birdTracks.ShiftAll(s=>OceanAnchorMath.RebasePose(oldAx,oldAz,oldAyaw,anchor.AnchorX,anchor.AnchorZ,anchor.AnchorYaw,s));
    Check(birdTracks.Sample(301L,0.5f,out var midBird)&&MathF.Abs(midBird.Y-10f)<0.001f,"airborne bird altitude lost during interpolation/rebase");
});
await Case("structural durability, 3-tier ballistics, fog-of-war vision, boarding defense, hull sliding and barter protocol",async()=>{
    // 1. Recipe material tiering, MaxHP, armor & 3-tier cannon ballistics
    Check(FleetMechanicsMath.ClassifyMaterialTier("WetWood")==1&&FleetMechanicsMath.ClassifyMaterialTier("Plastic")==1,"tier 1 materials");
    Check(FleetMechanicsMath.ClassifyMaterialTier("DryWood")==2&&FleetMechanicsMath.ClassifyMaterialTier("Planks")==2,"tier 2 materials");
    Check(FleetMechanicsMath.ClassifyMaterialTier("ScrapMetal")==3&&FleetMechanicsMath.ClassifyMaterialTier("Electronics")==3,"tier 3 materials");

    var plasticHutHp=FleetMechanicsMath.ComputeMaxStructuralHp(false,4,0,0);
    var steelWorkshopHp=FleetMechanicsMath.ComputeMaxStructuralHp(false,0,2,6);
    var heartHp=FleetMechanicsMath.ComputeMaxStructuralHp(true,0,4,4);
    Check(plasticHutHp==160f&&steelWorkshopHp>plasticHutHp*3f&&heartHp>=1400f,"recipe structural MaxHP scaling");

    var steelArmor=FleetMechanicsMath.ComputeArmorReduction(6,8);
    Check(steelArmor>0.35f&&steelArmor<=0.45f&&FleetMechanicsMath.ComputeArmorReduction(0,6)==0f,"armor reduction");

    var woodVsUnarmored=FleetMechanicsMath.ComputeCannonDamage(0,0f);
    var woodVsArmored=FleetMechanicsMath.ComputeCannonDamage(0,steelArmor);
    var scrapVsArmored=FleetMechanicsMath.ComputeCannonDamage(1,steelArmor);
    var empVsArmored=FleetMechanicsMath.ComputeCannonDamage(2,steelArmor);
    Check(woodVsArmored<woodVsUnarmored&&scrapVsArmored>woodVsArmored*2f&&MathF.Abs(empVsArmored-95f)<0.001f,"3-tier cannon damage vs armor");

    Check(MathF.Abs(FleetMechanicsMath.ApplyDamageWithFloor(200f,1000f,500f,true)-150f)<0.001f,"Townheart 15% floor");
    Check(FleetMechanicsMath.ApplyDamageWithFloor(80f,200f,120f,false)==0f,"normal building collapses to 0");
    Check(FleetMechanicsMath.IsCrippledMalfunction(110f,200f)&&!FleetMechanicsMath.IsCrippledMalfunction(130f,200f),"60% crippled threshold");
    Check(FleetMechanicsMath.IsTownheartLeakParalyzed(340f,1000f)&&!FleetMechanicsMath.IsTownheartLeakParalyzed(400f,1000f),"35% Townheart leak threshold");
    Check(FleetMechanicsMath.ComputeRepairCostCount(80f,200f,10)>=2&&FleetMechanicsMath.ComputeCollapseSalvageCount(7)==4,"repair and 50% collapse salvage counts");

    // 2. Per-ship Fog-of-War, WatchTower asymmetry, Signal Flare & Alliances
    Check(FleetMechanicsMath.GetVisionRadius(false)==400f&&FleetMechanicsMath.GetVisionRadius(true)==750f,"WatchTower asymmetric vision");
    Check(!FleetMechanicsMath.IsShipSpotted(0f,0f,400f,600f,0f,false,false),"unallied ship at 600m hidden from 400m hull");
    Check(FleetMechanicsMath.IsShipSpotted(0f,0f,750f,600f,0f,false,false),"WatchTower ship at 600m spots target");
    Check(FleetMechanicsMath.IsShipSpotted(0f,0f,400f,2500f,0f,true,false),"allied ship visible across map");
    Check(FleetMechanicsMath.IsShipSpotted(0f,0f,400f,2500f,0f,false,true),"flared ship visible across map");

    // 3. Dynamic Boarding Defense
    var undefended=FleetMechanicsMath.ResolveBoarding(1,false,0,0.9f,out var mult0,out var poll0);
    Check(undefended==BoardingOutcome.CleanSuccess&&mult0==1f&&poll0==0f,"undefended ship clean raid");
    var stealth1=FleetMechanicsMath.ResolveBoarding(2,true,1,0.8f,out var multStealth,out _);
    Check(stealth1==BoardingOutcome.CleanSuccess&&multStealth==1f,"stealth frogman bypasses lone sentry");
    var repelled=FleetMechanicsMath.ResolveBoarding(0,false,4,0.95f,out var multRep,out var pollRep);
    Check(repelled==BoardingOutcome.Repelled&&multRep==0f&&pollRep>20f,"heavily defended ship repels raider with pollution penalty");

    // 4. Multi-disc Hull Tangential Sliding & Ramming
    var hull=new[]{new HullDisc(0f,0f,12f),new HullDisc(0f,10f,8f)};
    var island=new[]{new HullObstacle(8f,18f,10f,isShip:false)};
    var collidedIsland=FleetMechanicsMath.SolveHullSlideAndRam(hull,0f,4f,island,12f,out var slideX,out var slideZ,out var islandRam,out var hitIdx);
    Check(collidedIsland&&hitIdx==0&&slideX<-0.1f&&slideZ>0.5f&&islandRam==0f,"ship must slide tangentially around island without ram damage");

    var enemyShip=new[]{new HullObstacle(0f,20f,11f,isShip:true,tag:2)};
    var rammed=FleetMechanicsMath.SolveHullSlideAndRam(hull,0f,4f,enemyShip,14f,out _,out _,out var shipRamDmg,out _);
    Check(rammed&&shipRamDmg>=30f,"high-speed head-on ship collision must deal ramming damage");

    // 5. Rules & Protocol wire round-trip for Barter, Flares, Alliances, Biome Readiness & Durability
    var noTradeRules=new Rules{trading=false,stealing=false,sabotage=false,friendlyFire=false};
    Check(noTradeRules.Allows("buildingRepair")&&noTradeRules.Allows("shipFlare")&&noTradeRules.Allows("shipAllyRequest")&&noTradeRules.Allows("nextBiomeReady"),"diplomacy/repair always allowed");
    Check(!noTradeRules.Allows("shipFloatBarrel")&&!noTradeRules.Allows("shipBarterOffer")&&!noTradeRules.Allows("shipBarterAccept"),"barter/barrel gated by trading rule");
    var yesTradeRules=new Rules{trading=true};
    Check(yesTradeRules.Allows("shipFloatBarrel")&&yesTradeRules.Allows("shipBarterOffer")&&yesTradeRules.Allows("shipBarterAccept")&&yesTradeRules.Allows("shipBarterCancel"),"trading enables barrel and barter");

    var v2Frame=new WorldFrame{
        biomeReadyPeers=new[]{"host","Ship:p2:乙"},
        biomeCountdown=11.5f,
        tradeOffers=new[]{new TradeOfferState{id=7,fromShip="host",fromOwner="房主",toShip="Ship:p2:乙",giveItem="DryWood",giveAmount=8,wantItem="ScrapMetal",wantAmount=4}},
        ships=new[]{new FleetShipState{name="host",flareUntil=125.5f,hasWatchTower=true,crippledHeart=true,allies=new[]{"Ship:p2:乙"}}},
        buildings=new[]{new BuildingState{id=12,structuralHp=145.5f,maxStructuralHp=320f,empUntil=88f,underRepair=true}},
        actions=new[]{new FleetActionState{id=9,kind="shipAttack",subKind=2}},
        landmarks=new[]{new LandmarkState{mapX=10f,mapZ=20f,remainingItems=new[]{new ItemCount{properties="ScrapMetal",section=4,count=6}}}}
    };
    using var s=new MemoryStream();
    await Frames.Write(s,new Packet{type="world",world=v2Frame},CancellationToken.None);
    using var fs=new FragmentedStream(s.ToArray());
    var rt=(await Frames.Read(fs,CancellationToken.None)).world!;
    Check(rt.biomeReadyPeers.Length==2&&MathF.Abs(rt.biomeCountdown-11.5f)<0.001f,"biome readiness round-trip");
    Check(rt.tradeOffers.Length==1&&rt.tradeOffers[0].giveItem=="DryWood"&&rt.tradeOffers[0].wantAmount==4,"barter offer round-trip");
    Check(rt.ships[0].hasWatchTower&&rt.ships[0].crippledHeart&&rt.ships[0].allies[0]=="Ship:p2:乙"&&MathF.Abs(rt.ships[0].flareUntil-125.5f)<0.001f,"ship visibility/alliance round-trip");
    Check(MathF.Abs(rt.buildings[0].structuralHp-145.5f)<0.001f&&MathF.Abs(rt.buildings[0].maxStructuralHp-320f)<0.001f&&rt.buildings[0].underRepair,"building durability round-trip");
    Check(rt.actions[0].subKind==2&&rt.landmarks[0].remainingItems[0].count==6,"action subKind and landmark remainingItems round-trip");
});
Console.WriteLine($"{passed} protocol tests passed");
var loadPassed=FleetLoadTests.Run();
Console.WriteLine($"{passed+loadPassed} protocol and 8-peer transport tests passed");

sealed class FragmentedStream(byte[] bytes):MemoryStream(bytes)
{
    public override Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)=>base.ReadAsync(buffer,offset,Math.Min(3,count),token);
}
