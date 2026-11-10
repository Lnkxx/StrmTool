using StrmTool;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Text.Json;
int count=0;
void Check(bool ok,string why) { if(!ok)throw new Exception(why);count++; }
void Reject(Action action,ProbeFailureKind kind) { try { action();throw new Exception("Expected rejection"); }catch(ProbeFailureException e){Check(e.Kind==kind,"classification");} }
Check(ProbeFailurePolicy.Classify(new HttpRequestException("sensitive-url",null,HttpStatusCode.Forbidden))==ProbeFailureKind.AccessDenied,"403");
Check(ProbeFailurePolicy.Classify(new HttpRequestException("",null,(HttpStatusCode)429))==ProbeFailureKind.RateLimited,"429");
Check(!ProbeFailurePolicy.ShouldRetry(ProbeFailureKind.RateLimited)&&!ProbeFailurePolicy.ShouldRetry(ProbeFailureKind.AccessDenied),"no immediate access retries");
Check(ProbeFailurePolicy.ShouldRetry(ProbeFailureKind.TransientNetwork),"network retry");
try { ProbeFailurePolicy.Classify(new OperationCanceledException());throw new Exception("cancellation suppressed"); }catch(OperationCanceledException){count++;}
var dir=Path.Combine(Path.GetTempPath(),"probe-upgrade-check-"+Guid.NewGuid());Directory.CreateDirectory(dir);
try {
 var file=Path.Combine(dir,"sample.strm");File.WriteAllText(file,"http://example.invalid/media");
 var stream=new MediaStream{Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitDepth=10,ColorPrimaries="bt2020"};
 var result=new MediaProbeResult{Size=1000000,RunTimeTicks=600000000,Container="mkv",StrmContentHash=MediaInfoCache.GetStrmContentHash(file),MediaStreams=new(){stream}};
 var native=new Video{Id=Guid.NewGuid(),Path=file,Size=1000000,RunTimeTicks=600000000,Container="mkv",Width=3840,Height=2160};
 TechnicalMetadataVerifier.Verify(native,file,result,new[]{stream});count++;
 native.Size=93;Reject(()=>TechnicalMetadataVerifier.Verify(native,file,result,new[]{stream}),ProbeFailureKind.PersistenceMismatch);native.Size=1000000;
 native.Width=1920;Reject(()=>TechnicalMetadataVerifier.Verify(native,file,result,new[]{stream}),ProbeFailureKind.PersistenceMismatch);native.Width=3840;
 native.RunTimeTicks=1;Reject(()=>TechnicalMetadataVerifier.Verify(native,file,result,new[]{stream}),ProbeFailureKind.PersistenceMismatch);native.RunTimeTicks=result.RunTimeTicks;
 Reject(()=>TechnicalMetadataVerifier.Verify(native,file,result,Array.Empty<MediaStream>()),ProbeFailureKind.PersistenceMismatch);
 var stale=new MediaStream{Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitDepth=8};
 Reject(()=>TechnicalMetadataVerifier.Verify(native,file,result,new[]{stale}),ProbeFailureKind.PersistenceMismatch);
 File.WriteAllText(file,"http://example.invalid/replaced");Reject(()=>TechnicalMetadataVerifier.Verify(native,file,result,new[]{stream}),ProbeFailureKind.SourceChanged);
 File.WriteAllText(file,"http://example.invalid/media");
 var cache=new MediaInfoCache(NullLogger.Instance);await cache.SaveFullCacheAsync(file,result.MediaStreams,result.Size,result.RunTimeTicks,result.Container,expectedStrmContentHash:result.StrmContentHash);
 Check(cache.TryGetFullCache(file,out var saved)&&saved.Version=="1.2"&&saved.ProbeVersion=="2.4.3.4"&&saved.CompletedStages.Contains("native-persistence-verified"),"versioned cache");
 var monitor=new RecordingMonitor();await new MediaInfoCache(NullLogger.Instance,monitor).SaveFullCacheAsync(file,result.MediaStreams,result.Size,result.RunTimeTicks,result.Container,expectedStrmContentHash:result.StrmContentHash);
 Check(monitor.Begun.Count==2&&monitor.Begun.Any(p=>p.EndsWith(".strmtool.json"))&&monitor.Begun.Any(p=>p.EndsWith(".tmp"))&&monitor.Begun.SetEquals(monitor.CompletedWithoutRefresh)&&monitor.Changed==0,"sidecar writes never trigger a library refresh");
 var id=Guid.NewGuid();using(var lease=await ProbeCoordinator.EnterAsync(id,default)){
  using var cts=new CancellationTokenSource(50);
  try { using var second=await ProbeCoordinator.EnterAsync(id,cts.Token);throw new Exception("concurrent writer admitted"); }catch(OperationCanceledException){count++;}
 }
 using(var lease=await ProbeCoordinator.EnterAsync(id,default)){count++;}
 Check(!TechnicalMetadataVerifier.ChaptersEqual(new[]{new ChapterInfo{Name="one",StartPositionTicks=1}},new[]{new ChapterInfo{Name="wrong",StartPositionTicks=1}}),"chapter name persistence");
 // Native re-probe kept dimensions but lost bitrate/bit depth: the cache must win.
 var rich=new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitRate=65886540,BitDepth=10},new(){Type=MediaStreamType.Audio,Index=1,Codec="dts",BitRate=3686198,Channels=6}};
 var nativeLoss=new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160},new(){Type=MediaStreamType.Audio,Index=1,Codec="dts",Channels=6}};
 Check(TechnicalMetadataVerifier.StoredStreamsDegraded(rich,nativeLoss),"bitrate loss detected");
 var externalSub=new MediaStream{Type=MediaStreamType.Subtitle,Index=2,Codec="srt",IsExternal=true,Path="/x.zho.srt"};
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(rich,new List<MediaStream>(rich){externalSub}),"external subtitle is not degradation");
 var sparse=new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160}};
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(sparse,new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitRate=1}}),"sparse cache never forces re-apply");
 // Probes that disagree are ambiguous (NAS cases: 变形金刚5 50 vs 20 streams, 白夜追凶 1740 vs 1746): keep native.
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(rich,rich.Take(1).ToList()),"different stream count is ambiguous, not degradation");
 Check(TechnicalMetadataVerifier.StoredStreamsDegraded(rich,new List<MediaStream>()),"no stored streams at all is degradation");
 var otherHeight=new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2154},nativeLoss[1]};
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(rich,otherHeight),"conflicting height blocks re-apply even with missing bitrate");
 var otherAudioRate=new List<MediaStream>{nativeLoss[0],new(){Type=MediaStreamType.Audio,Index=1,Codec="dts",BitRate=1509000,Channels=6}};
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(rich,otherAudioRate),"conflicting audio bitrate blocks re-apply");
 // Harmless format differences seen on the NAS do not block filling a missing video bitrate.
 var cachedSubs=new List<MediaStream>(rich){new(){Type=MediaStreamType.Subtitle,Index=2,Codec="SUBRIP"},new(){Type=MediaStreamType.Subtitle,Index=3,Codec="PGSSUB",Width=3840,Height=2160}};
 var storedSubs=new List<MediaStream>(nativeLoss){new(){Type=MediaStreamType.Subtitle,Index=2,Codec="subrip"},new(){Type=MediaStreamType.Subtitle,Index=3,Codec="PGSSUB",Width=1920,Height=1080}};
 Check(TechnicalMetadataVerifier.StoredStreamsDegraded(cachedSubs,storedSubs),"codec case and subtitle canvas size do not block re-apply");
 // Item-level restore only on a reset, never on a disagreement (NAS: 白夜追凶 1740 vs 1746 was overwritten by 2.4.2.3).
 var needsRestore=typeof(ItemUpdateListener).GetMethod("NeedsMetadataRestore",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
 var hvCache=new MediaInfoCacheData{Size=8535639718,RunTimeTicks=27000000000,MediaStreams=new(){new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=4096,Height=1746}}};
 var hvStored=new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=4096,Height=1740}};
 bool Restore(Video v)=>(bool)needsRestore.Invoke(null,new object[]{v,hvCache,hvStored})!;
 Check(!Restore(new Video{Size=8535639718,RunTimeTicks=27000000000,Width=4096,Height=1740}),"different real height is kept, not restored");
 Check(!Restore(new Video{Size=8535639000,RunTimeTicks=27000000000,Width=4096,Height=1740}),"different real size is kept, not restored");
 Check(Restore(new Video{Size=93,RunTimeTicks=27000000000,Width=4096,Height=1740}),"93-byte STRM size reset is restored");
 Check(Restore(new Video{Size=8535639718,RunTimeTicks=null,Width=4096,Height=1740}),"missing runtime is restored");
 var zeroDepth=new List<MediaStream>{new(){Type=MediaStreamType.Audio,Index=0,Codec="eac3",BitDepth=0,Channels=6}};
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(zeroDepth,new List<MediaStream>{new(){Type=MediaStreamType.Audio,Index=0,Codec="eac3",Channels=6}}),"cached 0 means unknown, not missing detail");
 // Real Jellyfin 10.11 layout (黑镜 S01E01 on the NAS): external subtitles are 0..n-1, embedded streams start at n.
 MediaStream Ext(int i,string codec)=>new(){Type=MediaStreamType.Subtitle,Index=i,Codec=codec,IsExternal=true,Path=$"/x.{i}.{codec}"};
 MediaStream Shifted(MediaStream s,int by)=>new(){Type=s.Type,Index=s.Index+by,Codec=s.Codec,Width=s.Width,Height=s.Height,BitRate=s.BitRate,BitDepth=s.BitDepth,Channels=s.Channels};
 var storedWithExt=new List<MediaStream>{Ext(0,"ass"),Ext(1,"subrip")};storedWithExt.AddRange(rich.Select(s=>Shifted(s,2)));
 Check(!TechnicalMetadataVerifier.StoredStreamsDegraded(rich,storedWithExt),"shifted indexes with same detail are not degradation");
 var lossWithExt=new List<MediaStream>{Ext(0,"ass"),Ext(1,"subrip")};lossWithExt.AddRange(nativeLoss.Select(s=>Shifted(s,2)));
 Check(TechnicalMetadataVerifier.StoredStreamsDegraded(rich,lossWithExt),"bitrate loss detected behind external subtitles");
 Check(TechnicalMetadataVerifier.StreamsEqual(rich,storedWithExt),"verify pairs embedded streams by position");
 Check(TechnicalMetadataVerifier.StreamsEqual(rich,new List<MediaStream>(rich){externalSub}),"verify ignores external subtitles");
 var merged=TechnicalMetadataVerifier.WithStoredExternal(rich,lossWithExt);
 Check(merged.Count==4&&merged[0].IsExternal&&merged[0].Index==0&&merged[1].IsExternal&&merged[1].Index==1
  &&!merged[2].IsExternal&&merged[2].Index==2&&merged[2].BitRate==65886540&&merged[3].Index==3,"externals first, embedded shifted, cached detail kept");
 Check(rich[0].Index==0&&rich[1].Index==1,"cached streams are not renumbered in place");
 Check(TechnicalMetadataVerifier.StreamsEqual(rich,merged)&&!TechnicalMetadataVerifier.StoredStreamsDegraded(rich,merged),"merged result verifies and is stable");
 Check(ReferenceEquals(TechnicalMetadataVerifier.WithStoredExternal(rich,nativeLoss)[0],rich[0]),"no externals: streams pass through unchanged");
 // Jellyfin persists no ColorRange column: a cached "tv" read back as null must still verify.
 var probedRange=new MediaStream{Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitDepth=10,ColorRange="tv"};
 var readBack=new MediaStream{Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitDepth=10};
 Check(TechnicalMetadataVerifier.StreamsEqual(new[]{probedRange},new[]{readBack}),"ColorRange not persisted by Jellyfin is ignored");
 var rangeResult=new MediaProbeResult{Size=result.Size,RunTimeTicks=result.RunTimeTicks,Container=result.Container,StrmContentHash=result.StrmContentHash,MediaStreams=new(){probedRange}};
 TechnicalMetadataVerifier.Verify(native,file,rangeResult,new[]{readBack});count++;
 // Fields that Jellyfin does persist must still be compared.
 Check(!TechnicalMetadataVerifier.StreamsEqual(new[]{probedRange},new[]{new MediaStream{Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=3840,Height=2160,BitDepth=8}}),"persisted field mismatch still rejected");
 // Jellyfin 10.11 read-back conversions seen on the NAS trial (星际穿越: chi/cze/dut/fre/ger/gre/rum).
 var cachedLang=new MediaStream{Type=MediaStreamType.Audio,Index=0,Codec="dts",Language="chi",Level=5.1,IsInterlaced=false};
 var readLang=new MediaStream{Type=MediaStreamType.Audio,Index=0,Codec="dts",Language="zho",Level=(float)5.1,IsInterlaced=false};
 Check(TechnicalMetadataVerifier.StreamsEqual(new[]{cachedLang},new[]{readLang}),"ISO 639-2/B language and float Level read-back verify");
 Check(!TechnicalMetadataVerifier.StreamsEqual(new[]{cachedLang},new[]{new MediaStream{Type=MediaStreamType.Audio,Index=0,Codec="dts",Language="eng",Level=5.1}}),"real language change still rejected");
 // Legacy 1.0 cache (no STRM hash) is adopted only while the STRM is unchanged since it was written.
 var cachePath=Path.Combine(dir,"sample.strmtool.json");
 var legacy=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cachePath))!.AsObject();
 var hashKey=legacy.Select(p=>p.Key).First(k=>k.Equals("strmContentHash",StringComparison.OrdinalIgnoreCase));
 legacy.Remove(hashKey);legacy["version"]="1.0";legacy["timestamp"]=DateTime.UtcNow.AddMinutes(5);File.WriteAllText(cachePath,legacy.ToJsonString());
 Check(cache.TryGetFullCache(file,out var adopted)&&adopted.StrmContentHash==MediaInfoCache.GetStrmContentHash(file),"unchanged legacy cache adopted");
 Check(!File.ReadAllText(cachePath).Contains(MediaInfoCache.GetStrmContentHash(file)!),"legacy adoption does not rewrite the cache file");
 File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddHours(1));
 Check(!cache.TryGetFullCache(file,out _),"legacy cache rejected after STRM modification");
 Console.WriteLine($"PASS {count} reliability checks");
} finally { Directory.Delete(dir,true); }
sealed class RecordingMonitor:MediaBrowser.Controller.Library.ILibraryMonitor {
 public HashSet<string> Begun=new(),CompletedWithoutRefresh=new();public int Changed;
 public void Start(){} public void Stop(){} public void Dispose(){}
 public void ReportFileSystemChangeBeginning(string path)=>Begun.Add(path);
 public void ReportFileSystemChangeComplete(string path,bool refreshPath){if(!refreshPath)CompletedWithoutRefresh.Add(path);else Changed++;}
 public void ReportFileSystemChanged(string path)=>Changed++;
}
