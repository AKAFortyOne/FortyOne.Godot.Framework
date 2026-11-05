## 除非你明确需要改善代码体验，否则你不应该增改也不需要阅读此模块的代码。
## 如果你只是在找该模块添加了哪些功能以及他们如何在C#中使用，请参考[docs/modules/mono/README.md](../../../docs/modules/mono/README.md)

此模块通过Incremental Source Generation（增量源码生成）实现Compile-time Metaprogramming（编译时元编程），用于改善C#代码编写体验，减少样板代码，等等。

---

### 已添加的生成器：
- GameEventsGenerator.cs: 用于自动根据定义的Signal信号生成对应的Subscribe订阅方法。

---

如要参与编写Source Generator，参考：[How to contribute to Source Generators](docs/README.md)