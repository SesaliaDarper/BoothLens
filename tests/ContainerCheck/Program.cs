using System.Net;
using System.Text;
using System.Text.Json;
using BoothLens.Core;
using BoothLens.Core.Booth;
using BoothLens.Core.Data;
using BoothLens.Core.Library;

var work = Path.Combine(Path.GetTempPath(), "BoothLensContainers-" + Guid.NewGuid().ToString("N"));
var data = new AppPaths(Path.Combine(work,"data"));
var root = Path.Combine(work,"library");
string Folder(string cat,string name) { var p=Path.Combine(root,cat,name);Directory.CreateDirectory(p);return p; }
var salt=Folder("0角色","Salt_v1.0.0 (修订版)");
Directory.CreateDirectory(Path.Combine(salt,"Salt_v1.0.0","Materials"));
Directory.CreateDirectory(Path.Combine(salt,"Salt_v1.0.0","Texture"));
File.WriteAllText(Path.Combine(salt,"Salt_v1.0.0","Salt.unitypackage"),"fixture");
File.WriteAllText(Path.Combine(salt,"Salt_Eye_Hair_ColorPack.zip"),"fixture");
File.WriteAllText(Path.Combine(salt,"Salt_v1.0.0","Texture","texture.png"),"fixture");
var numbered=Folder("0衣服","009 - TechPaka_Alpha_Full 3629714");
Directory.CreateDirectory(Path.Combine(numbered,"nested-inner-resource"));
File.WriteAllText(Path.Combine(numbered,"nested-inner-resource","component.unitypackage"),"fixture");
var exact=Folder("0衣服","1234567 完全不同的本地名称");
var invalid=Folder("0衣服","7654321 CottonDress_v1.2");
var transient=Folder("0衣服","9988776 Transient");
var hair=Folder("0发型","HairSet_v1.0");
File.WriteAllText(Path.Combine(root,"0发型","HairSet_v1.0.zip"),"fixture");
File.WriteAllText(Path.Combine(root,"0发型","ignored.png"),"fixture");
File.WriteAllText(Path.Combine(root,"0发型","ignored.fbx"),"fixture");
var db = new Db(data.DbPath); db.EnsureSchema();
var store = new ItemStore(db);
var fake=new FakeBooth();using var booth = new BoothClient(fake){ThrottleMs=0};
var catalog = new Catalog(booth,store,data);
var library = new ContainerLibrary(db,store,catalog,booth);
JsonElement Json(object x)=>JsonSerializer.SerializeToElement(x,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
void Check(string name,bool ok) {if(!ok)throw new Exception(name);Console.WriteLine("PASS "+name);}
async Task Wait() {for(int i=0;i<500;i++){if(Json(library.GetStatus()).GetProperty("state").GetString()!="running")return;await Task.Delay(20);}throw new Exception("Scan timed out");}
library.Start(root,"library",true); await Wait();
var all=Json(library.List(null,1,100,null));
var files=all.GetProperty("files").EnumerateArray().ToArray();
JsonElement Find(string p)=>files.Single(f=>f.GetProperty("path").GetString()==p);
Check("one container per main resource, not nested materials",files.Length==6);
Check("numeric zero prefix is not mistaken for a category",Find(numbered).GetProperty("category").GetString()=="0衣服");
Check("Salt never split into color or nested folders",files.Count(f=>f.GetProperty("path").GetString()!.StartsWith(salt))==1);
Check("exact remote ID binds despite unrelated local name",Find(exact).GetProperty("itemId").GetInt64()==1234567);
Check("name results require confirmation",Find(salt).GetProperty("status").GetString()=="review" && Find(salt).GetProperty("itemId").ValueKind==JsonValueKind.Null);
Check("invalid number falls through to name search",Find(invalid).GetProperty("status").GetString()=="review");
Check("network error does not fall through as missing ID",Find(transient).GetProperty("recognitionState").GetString()=="error" && !fake.Searches.Any(q=>q.Contains("Transient",StringComparison.OrdinalIgnoreCase)));
Check("sibling archive grouped with folder",Find(hair).GetProperty("aliases").GetArrayLength()>=1);
Check("search candidates do not download covers",booth.ImageRequestCount==2);
await catalog.Add("2345678",default);library.Confirm(salt,2345678);
var source=library.PrepareImport(salt,Path.Combine(salt,"Salt_v1.0.0","Salt.unitypackage"));
Check("explicit member import registered with container identity",store.GetLocalFile(source)?.ItemId==2345678);
var searchesBefore=fake.Searches.Count;var imagesBefore=booth.ImageRequestCount;
library.Start(root,"library",true);await Wait();
Check("online repeat reuses pending name candidates and confirmed covers",fake.Searches.Count==searchesBefore && booth.ImageRequestCount==imagesBefore);
var count=booth.RequestCount;
library.Start(root,"library",false);await Wait();
Check("offline repeat does not network",booth.RequestCount==count);
var confirmed=Json(library.Get(salt)!);
Check("manual container binding survives repeat scan",confirmed.GetProperty("itemId").GetInt64()==2345678);
try{library.PrepareImport(salt,Path.Combine(root,"0发型","HairSet_v1.0.zip"));throw new Exception("out of container accepted");}catch(ArgumentException){Console.WriteLine("PASS outside member blocked");}
var beforeUnbind = booth.RequestCount;
var membersBefore = library.Get(salt)!.Members.Count;
library.Unbind(salt);
Check("unbind clears manual product metadata and inherited member link", library.Get(salt)!.ItemId == null && library.Get(salt)!.Item == null && store.GetLocalFile(source)?.ItemId == null);
Check("unbind retains resource members and performs no network", library.Get(salt)!.Members.Count == membersBefore && booth.RequestCount == beforeUnbind);
library.Start(root,"library",true); await Wait();
Check("ordinary online scan does not restore cleared manual binding", library.Get(salt)!.ItemId == null);
library.Unbind(exact); library.Start(root,"library",true); await Wait();
Check("ordinary scan does not restore cleared numeric binding", library.Get(exact)!.ItemId == null);
Check("unbound member import is registered without product identity", library.PrepareImport(salt,source) == source && store.GetLocalFile(source)?.ItemId == null && library.Get(salt)!.RecognitionState == "unknown");
library.Confirm(salt,2345678);
Check("new manual binding works after unbind", library.Get(salt)!.ItemId == 2345678);
library.Retry(exact); library.Start(root,"library",true); await Wait();
Check("explicit retry may identify again", library.Get(exact)!.ItemId == 1234567);
try { library.Unbind(Path.Combine(work,"unknown")); throw new Exception("unknown resource accepted"); } catch(KeyNotFoundException) { Console.WriteLine("PASS unknown unbind blocked"); }
Console.WriteLine("CONTAINER CHECKS PASSED");
sealed class FakeBooth:HttpMessageHandler
{
 public List<string> Searches {get;} = new();
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,CancellationToken ct)
 {
  var u=req.RequestUri!;
  if(u.Host=="booth.pximg.net") {var image=new ByteArrayContent(new byte[]{255,216,255,217});image.Headers.ContentType=new("image/jpeg");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=image});}
  var path=Uri.UnescapeDataString(u.AbsolutePath);
  if(path.Contains("/search/")){Searches.Add(path);var html="<li class='item-card' data-product-id='2345678' data-product-name='Salt avatar' data-product-brand='test' data-product-price='100'><a class='item-card__thumbnail-image' data-original='https://booth.pximg.net/test.jpg'></a></li>";return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(html)});}
  if(path.Contains("7654321"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
  if(path.Contains("9988776"))return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
  var id=long.Parse(System.Text.RegularExpressions.Regex.Match(path,@"items/(\d+)").Groups[1].Value);
  var value=new{id,name="Valid BOOTH resource "+id,url=$"https://test.booth.pm/items/{id}",images=new[]{new{original=$"https://booth.pximg.net/{id}.jpg"}},shop=new{name="Test",subdomain="test"}};
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")});
 }
}
