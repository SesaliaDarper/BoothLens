using BoothLens.Core.Library;
using System.Text.Json;
var root=Path.Combine(Path.GetTempPath(),"BoothLensEditors-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var registry=Path.Combine(root,"registry");Directory.CreateDirectory(registry);
var live=new HashSet<int>{101,102,103};
var editors=new UnityEditors(registry,(pid,ticks)=>live.Contains(pid)&&ticks==1000+pid);
void Write(int pid,string name,bool busy=false,bool active=false,int age=0){var project=Path.Combine(root,name);Directory.CreateDirectory(Path.Combine(project,"Assets"));Directory.CreateDirectory(Path.Combine(project,"ProjectSettings"));File.WriteAllText(Path.Combine(project,"ProjectSettings","ProjectVersion.txt"),"2022.3");var h=new{id=$"{pid}-{pid+1000}",projectPath=project,processId=pid,processStartTicks=pid+1000,updatedUtc=DateTimeOffset.UtcNow.AddSeconds(-age).ToString("O"),busy,isActive=active};File.WriteAllText(Path.Combine(registry,h.id+".json"),JsonSerializer.Serialize(h));}
void Check(string name,bool condition){if(!condition)throw new Exception(name);Console.WriteLine("PASS "+name);}
void Reject(string name,Action f){try{f();}catch(ArgumentException){Console.WriteLine("PASS "+name);return;}throw new Exception(name);}
Reject("zero editor blocks import without stale path fallback",()=>editors.Resolve(null));
Write(101,"ProjectA");Check("single live editor auto resolves",editors.Resolve(null).Name=="ProjectA");
Write(102,"ProjectB",active:true);Check("active editor prioritized in UI list",editors.List()[0].Name=="ProjectB");Reject("multiple editors require explicit target",()=>editors.Resolve(null));
Check("explicit choice resolves correct project",editors.Resolve("101-1101").Name=="ProjectA");
Write(103,"BusyProject",busy:true);Reject("busy editor blocks import",()=>editors.Resolve("103-1103"));
Write(104,"DeadProject");Write(105,"StaleProject",age:50);Check("dead and stale registrations filtered",editors.List().Count==3);
live.Remove(101);Reject("closed editor rejected even previously selected",()=>editors.Resolve("101-1101"));
Console.WriteLine("EDITOR CHECKS PASSED; simulated registry, no Unity Editor imported");
