using BoothLens.Editor;
foreach(var (source,callback) in new[]{
 ("C:/Stage/IconGeneratorToolVer1.2.unitypackage","IconGeneratorToolVer1.2"),
 ("C:/Stage/Kaguya_v1.06.unitypackage","Kaguya_v1.06"),
 ("C:/Stage/Foo.v1.2.3.unitypackage","Foo.v1.2.3.unitypackage"),
 ("C:/Stage/图标工具Ver1.2.unitypackage","图标工具Ver1.2"),
 ("C:/Stage/Demo.UNITYPACKAGE","Demo"),
 ("C:/Stage/Demo.unitypackage","Demo.unitypackage")}) {
 if(PackageNames.Normalize(source)!=PackageNames.Normalize(callback))throw new Exception(source+" != "+callback);
 Console.WriteLine("PASS callback "+callback);
}
if(PackageNames.Normalize("A.1")==PackageNames.Normalize("A.2"))throw new Exception("version collapsed");
Console.WriteLine("BRIDGE PACKAGE CALLBACK CHECKS PASSED");
