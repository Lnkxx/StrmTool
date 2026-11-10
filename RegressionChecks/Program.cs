using StrmTool;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Entities;
using System.Text.Json;
var dir = Path.Combine(Path.GetTempPath(), "media-repair-checks-" + Guid.NewGuid());
Directory.CreateDirectory(dir);
Console.WriteLine(typeof(MediaInfoCache).Assembly.FullName);
int checks = 0;
void Check(bool ok, string reason) { if (!ok) throw new Exception(reason); checks++; }
try
{
 var first = Path.Combine(dir,"one.strm"); var second = Path.Combine(dir,"two.strm"); var third = Path.Combine(dir,"other.strm");
 File.WriteAllText(first,"http://example.invalid/one"); File.WriteAllText(second,"http://example.invalid/two");
 var cfg = new PluginConfiguration { ProbePathFilter = first };
 Check(cfg.IsPathInProbeScope(first) && !cfg.IsPathInProbeScope(second), "single-file scope");
 var manifest = Path.Combine(dir,"targets.txt");File.WriteAllLines(manifest,new[]{first,second});cfg.ProbePathFilter="@"+manifest;
 Check(cfg.IsPathInProbeScope(first)&&cfg.IsPathInProbeScope(second)&&!cfg.IsPathInProbeScope(third),"exact allowlist scope");
 Check(!cfg.IsPathInProbeScope(first+".extra"),"no prefix expansion");
 cfg.ProbePathFilter="@"+Path.Combine(dir,"missing.txt");Check(!cfg.IsPathInProbeScope(first),"missing manifest fails closed");
 File.WriteAllText(manifest,"relative.strm");cfg.ProbePathFilter="@"+manifest;Check(!cfg.IsPathInProbeScope(first),"malformed manifest fails closed");
 File.WriteAllText(manifest,"");File.SetLastWriteTimeUtc(manifest,DateTime.UtcNow.AddSeconds(2));Check(!cfg.IsPathInProbeScope(first),"empty manifest fails closed");
 var cache = new MediaInfoCache(NullLogger.Instance);
 var streams = new[]{new MediaStream{Type=MediaStreamType.Video,Codec="h264",Width=1920,Height=1080,Index=0},new MediaStream{Type=MediaStreamType.Audio,Codec="aac",Index=1}};
 var hash = MediaInfoCache.GetStrmContentHash(first);
 await cache.SaveFullCacheAsync(first,streams,10000000,100000000,"mp4",expectedStrmContentHash:hash);
 Check(cache.TryGetFullCache(first,out var valid)&&valid.Size==10000000&&valid.StrmContentHash==hash,"valid fresh cache");
 File.Copy(Path.ChangeExtension(first,".strmtool.json"),Path.ChangeExtension(second,".strmtool.json"),true);
 Check(!cache.TryGetFullCache(second,out _),"copied cache cannot follow another target");
 File.WriteAllText(first,"http://example.invalid/replaced");Check(!cache.TryGetFullCache(first,out _),"target rewrite invalidates cache");
 File.WriteAllText(Path.ChangeExtension(first,".strmtool.json"),"{\"isValid\":true,\"size\":79,\"runTimeTicks\":10000000,\"mediaStreams\":[{\"Type\":1,\"Codec\":\"h264\"}]}");
 Check(!cache.TryGetFullCache(first,out _),"small incorrect legacy size is invalid");
 File.WriteAllText(Path.ChangeExtension(first,".strmtool.json"),"{\"isValid\":true,\"size\":10000000,\"runTimeTicks\":10000000,\"mediaStreams\":[{\"Type\":1,\"Codec\":\"h264\",\"Width\":1920,\"Height\":1080}]}");
 Check(!cache.TryGetFullCache(first,out _),"unbound legacy cache cannot overwrite native metadata");
 File.WriteAllText(Path.ChangeExtension(first,".strmtool.json"),"{\"isValid\":true,\"size\":10000000,\"runTimeTicks\":10000000,\"mediaStreams\":[]}");
 Check(!cache.TryGetFullCache(first,out _),"empty media streams rejected");
 Check(!new MediaProbeResult{MediaStreams=new(){new MediaStream{Type=MediaStreamType.Subtitle}}}.Success,"subtitle-only probe is not a video success");
 File.WriteAllText(first,"http://example.invalid/dimensions");
 await cache.SaveFullCacheAsync(first,new[]{new MediaStream{Type=MediaStreamType.Video,Codec="h264",Index=0}},10000000,100000000,"mp4",expectedStrmContentHash:MediaInfoCache.GetStrmContentHash(first));
 Check(!cache.TryGetFullCache(first,out _),"video cache without dimensions rejected");
 Console.WriteLine($"PASS {checks} behavior checks");
}
finally { Directory.Delete(dir,true); }
