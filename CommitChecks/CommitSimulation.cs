using System.Reflection;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using StrmTool;
using static ProxyFactory;

internal sealed class CommitVideo : Video
{
 public Func<CommitVideo,Task> Submit {get;set;} = _=>Task.CompletedTask;
 public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken token) { token.ThrowIfCancellationRequested(); return Submit(this); }
}
internal static class CommitSimulation
{
 public static async Task<int> Run()
 {
  int checks=0;
  foreach(string scenario in new[]{"success","readback-failure","source-change","path-change","empty-chapters"})
  {
   string directory=Path.Combine(Path.GetTempPath(),"commit-sim-"+Guid.NewGuid());Directory.CreateDirectory(directory);
   try
   {
    string path=Path.Combine(directory,"one.strm"), next=Path.Combine(directory,"two.strm");File.WriteAllText(path,"https://example.invalid/one");File.WriteAllText(next,"https://example.invalid/two");
    var live=new CommitVideo {Id=Guid.NewGuid(),Path=path,Size=9000000,RunTimeTicks=900000000,Container="mkv",Width=1280,Height=720,TotalBitrate=800000};
    Video Copy(Video v)=>new Video{Id=v.Id,Path=v.Path,Size=v.Size,RunTimeTicks=v.RunTimeTicks,Container=v.Container,Width=v.Width,Height=v.Height,TotalBitrate=v.TotalBitrate,DateLastSaved=v.DateLastSaved};
    var stored=Copy(live);
    var oldStreams=new List<MediaStream>{new(){Type=MediaStreamType.Video,Index=0,Codec="h264",Width=1280,Height=720}};
    IReadOnlyList<MediaStream> streams=oldStreams.ToList();IReadOnlyList<ChapterInfo> chapters=new List<ChapterInfo>{new(){StartPositionTicks=123}};
    int submissions=0;
    live.Submit = current=> {
     submissions++;current.DateLastSaved=DateTime.UtcNow;
     if(submissions==1 && scenario=="source-change")File.WriteAllText(path,"https://example.invalid/replaced");
     if(submissions==1 && scenario=="path-change")current.Path=next;
     stored=Copy(current);
     if(submissions==1 && scenario=="readback-failure")stored.Width=0;
     return Task.CompletedTask;
    };
    var library=CreateProxy<ILibraryManager>((m,a)=>m.Name=="GetItemById"?live:Program.DefaultValue(m.ReturnType));
    var itemRepo=CreateProxy<IItemRepository>((m,a)=>m.Name=="RetrieveItem"?Copy(stored):Program.DefaultValue(m.ReturnType));
    var streamRepo=CreateProxy<IMediaStreamRepository>((m,a)=> {
     if(m.Name=="SaveMediaStreams")streams=((IEnumerable<MediaStream>)a![1]!).ToList();
     return m.Name=="GetMediaStreams"?streams:Program.DefaultValue(m.ReturnType);
    });
    var chapterRepo=CreateProxy<IChapterRepository>((m,a)=> {
     if(m.Name=="SaveChapters")chapters=((IEnumerable<ChapterInfo>)a![1]!).ToList();
     if(m.Name=="DeleteChaptersAsync")chapters=Array.Empty<ChapterInfo>();
     return m.Name=="GetChapters"?chapters:Program.DefaultValue(m.ReturnType);
    });
    var encoder=CreateProxy<IMediaEncoder>((m,a)=>Program.DefaultValue(m.ReturnType));
    var service=new StrmMediaInfoService(library,encoder,streamRepo,chapterRepo,NullLogger.Instance,itemRepo);
    var candidate=new MediaProbeResult{SourcePath=path,StrmContentHash=MediaInfoCache.GetStrmContentHash(path),Size=4000000,RunTimeTicks=600000000,Container="mkv",ChaptersProbed=true,
     MediaStreams=new(){new(){Type=MediaStreamType.Video,Index=0,Codec="hevc",Width=1920,Height=1080,BitDepth=10}},Chapters=scenario=="empty-chapters"?new():new(){new(){StartPositionTicks=456}}};
    Exception? failure=null;try{await service.ApplyProbeResultAsync(live,candidate,default);}catch(Exception e){failure=e;}
    void Check(bool ok,string label){if(!ok)throw new Exception(scenario+": "+label);checks++;}
    if(scenario is "success" or "empty-chapters") {
     Check(failure==null&&candidate.PersistenceVerified,"verified happy path");Check(stored.Size==4000000&&stored.Width==1920,"stored technical fields");
     Check(chapters.Count==(scenario=="empty-chapters"?0:1),"authoritative empty chapter handling");
    } else if(scenario=="readback-failure") {
     Check(failure is ProbeFailureException,"commit rejected");Check(stored.Size==9000000&&stored.Width==1280,"old fields restored");
     Check(TechnicalMetadataVerifier.StreamsEqual(oldStreams,streams),"old streams restored");Check(chapters.Count==1&&chapters[0].StartPositionTicks==123,"old chapters restored");
    } else {
     Check(failure is ProbeFailureException,"source race rejected");Check(stored.RunTimeTicks==null&&stored.Width==0&&streams.Count==0,"stale fields invalidated");
     Check(chapters.Count==0,"stale chapters removed");Check(stored.Path==(scenario=="path-change"?next:path),"new path preserved");
    }
   } finally {Directory.Delete(directory,true);}
  }
  Console.WriteLine($"PASS {checks} commit/rollback simulation checks");return checks;
 }
}
