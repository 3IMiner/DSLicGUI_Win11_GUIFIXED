# DSLicGUI_Win11_GUIFIXED

解决 Windows 10 1903 及更高版本 / Windows 11 上 DSLicGUI（"许可证管理工具"）无法打开的问题。
本文修改对象以2016 DS LICENSE SERVER所修改

## 问题原因

`DSLicSrv.exe` 不是普通 exe，是 jar2exe / BoxedApp 壳：应用自己的 Java 类加密塞在 exe 里，由 `Jar2ExeClassLoader` 解密加载，壳的 native 是 `RebuildLS.dll`，BoxedApp 虚拟化 SDK `bxsdk32.dll` 也在进程内。这个壳是 2016 年编译的，在 Windows 10 1903 之后产生了如下问题：

```text
AdminMain.initializeUI
  -> JTable.addNotify
    -> DropTarget.addNotify
      -> WComponentPeer.addDropTarget
        -> addNativeDropTarget      (native, awt.dll)
          -> OleInitialize / RegisterDragDrop
```

表格要挂拖放目标，AWT 于是向 OLE 注册；但进程的 COM 单元早被壳摆过状态，1903 起这条 shell/COM 路径的行为又变了，握手失败变成在窗口过程回调里抛未处理异常，系统直接终止进程：

```text
0xC000041D = STATUS_FATAL_USER_CALLBACK_EXCEPTION
```

因为是操作系统在手写回调里击杀，JVM 的崩溃处理器来不及运行 —— 没有 Java 异常栈、也没有 `hs_err_pid*.log`。表现有两种（同一个死点）：约 4 秒内进程消失；或者挂住不返回，窗口永远不来。


### 替换 `jre\lib\rt.jar`

只改 JRE 里一个类的 6 个字节：`sun/awt/windows/WComponentPeer.class`

```text
addDropTarget:    9: invokevirtual addNativeDropTarget:()J   ->  lconst_0; nop; nop
removeDropTarget: 18: invokevirtual removeNativeDropTarget:()V ->  nop; nop; nop
```

即：不再向 OLE 注册原生拖放目标。安全性：

- 栈平衡完全一致（原指令压入一个 long，`lconst_0` 也压入一个 long；remove 路径净入栈 0）
- 不移动任何分支目标（两条改动均等长 3 字节，且都在跳转目标之前）
- 跳转目标处的 `StackMapTable` 帧本来就是空栈，校验器照常通过
- 补丁后 `nativeDropTargetContext` 恒为 0，将来即使调 `removeDropTarget` 也不会去 native 释放

### 使用方法

1. 结束所有 DSLicSrv 进程

   ```bat
   taskkill /f /im DSLicSrv.exe
   sc stop "DS License Server"
   ```

2. 替换 JAR 文件

   将 `rt.jar` 替换到以下目录：

   ```text
   ...\Dassault Systemes\DS License Server\intel_a\code\bin\intel_a\code\bin\intel_a\code\bin\jre\lib
   ```

3. 启动 DSLicSrv

   ```bat
   sc start "DS License Server"
   "...\Dassault Systemes\DS License Server\intel_a\code\bin\intel_a\code\bin\DSLicSrv.exe" /test -adminUI
   ```
