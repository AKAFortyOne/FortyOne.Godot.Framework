
### Q&A；
1. 如何断点调试？
- 在任意你需要打断点的地方加入以下代码：
	```csharp
	if (!System.Diagnostics.Debugger.IsAttached) 
	    System.Diagnostics.Debugger.Launch();
	```
	
	如果你需要Jetbrains Rider作为断点调试的首选工具而不是VS，你需要：
	1. 以管理员身份运行Rider
    2. 打开设置，搜索"just-in-time"，找到"Set Rider as the default debugger"
    3. 单击，保存
    4. 运行(Run)/调试(Debug)，均可开始断点调试。
2. 生成的代码在哪儿？
   - 解决方案：Rabbit Farm -> Dependencies -> .NET 8.0 -> Source Generator -> SourceGenerator.XXXXX

---

### 参考：
*如果你需要增改Source Generator，下面这些东西可能对你有用。*
- [Roslyn](https://github.com/dotnet/roslyn),[Roslyn Doc](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/) Source Generator 官方仓库及文档
- [Godot.NET.Sdk](https://github.com/godotengine/godot/tree/master/modules/mono/editor/Godot.NET.Sdk) 参考Godot的Source Generator的编写方式，便于做出和Godot C#一致的无缝的智能补全体验。
- [Syndiesis](https://github.com/Rekkonnect/Syndiesis) C# 语法语义分析工具。如果你完全看不懂Source Generator的编写逻辑以及筛选方式，**强烈推荐使用此工具获取语法树与语义树来参考。**