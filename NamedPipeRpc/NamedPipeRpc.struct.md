# NamedPipeRpc 结构关系

本文档用图说明库内核心类型关系与运行时数据流。使用说明见 [README.md](README.md)。

## 类关系

```mermaid
classDiagram
  class RpcService {
    +string PipeName
    +HandlerRouter Router
    +PipePeers Peers
    +Start()
    +Close()
  }
  class RpcClient {
    +string PipeName
    +HandlerRouter Router
    +ConnectAsync() NamedPipePeer
  }
  class NamedPipePeer {
    +Request()
    +Notify()
    +SubscribeAsync()
    +ListRemoteRequestMethodsAsync()
    +ListRemoteNotifyMethodsAsync()
  }
  class PipePeers {
    +Notify()
    +NotifySubscribers()
    +Request()
  }
  class HandlerRouter {
    +AddRequestHandler()
    +AddNotifyHandler()
    +RemoveRequestHandler()
    +RemoveNotifyHandler()
  }
  class RpcContext~T~ {
    +T Message
    +NamedPipePeer Peer
    +string Method
    +long MessageId
  }

  RpcService --> HandlerRouter : 本端目录
  RpcService --> PipePeers : 已连接集合
  PipePeers --> NamedPipePeer : 聚合
  RpcClient --> HandlerRouter : 本端目录
  RpcClient --> NamedPipePeer : ConnectAsync 创建
  NamedPipePeer --> HandlerRouter : 入站调度
  HandlerRouter ..> RpcContext~T~ : Handle 入参
```

要点：

- 服务端：`RpcService` 接受连接后创建 `NamedPipePeer` 并加入 `Peers`。
- 客户端：`RpcClient.ConnectAsync` 返回已 `Start` 的 `NamedPipePeer`。
- 两端各自持有独立 `HandlerRouter`；对端目录不靠共享 Router，而靠 Sys。

## 连接与消息流

```mermaid
flowchart TD
  subgraph server [服务端]
    StartSvc[RpcService.Start]
    Accept[ConnectTask 接受连接]
    NewPeer[创建 NamedPipePeer]
    AddPeers[Peers.Add]
    StartSvc --> Accept
    Accept --> NewPeer
    NewPeer --> AddPeers
  end

  subgraph client [客户端]
    Connect[RpcClient.ConnectAsync]
    ClientPeer[创建 NamedPipePeer]
    Connect --> ClientPeer
  end

  subgraph peerLoops [NamedPipePeer.Start 三循环]
    RecieveTask[RecieveTask 读帧入 Channel]
    SendTask[SendTask 写出 Channel]
    MsgHandleTask[MsgHandleTask 按 Type 分发]
  end

  NewPeer --> peerLoops
  ClientPeer --> peerLoops

  MsgHandleTask -->|Req| RequestHandler[Router Request Handler]
  MsgHandleTask -->|Ntf| NotifyHandler[Router Notify Handler]
  MsgHandleTask -->|Sys| SysHandle[列表 / 订阅 / 开闭推送]
  MsgHandleTask -->|Res Err UnK| WaitingRes[完成等待中的 Request]
  MsgHandleTask -->|Can| CancelHandler[取消对应 Handler CTS]
```

帧顺序：`MsgHeader(16)` → Method(UTF-8) → Payload。写出时先预留 Header，写完 Method/Payload 再回填。

## 订阅与定向推送

推荐路径：连接前 `AddNotifyHandler`（登记本端接收 + 期望订阅清单）→ `ConnectAsync` / `Start` 自动 `SubscribeAsync`。勿把「事后手动 Subscribe」当作默认用法（仅用于连接后动态增订）。

```mermaid
flowchart LR
  subgraph clientSide [客户端 Peer]
    Desired[_desiredNotifies 期望集]
    LocalNotify[本端已注册 Notify]
    SubAsync[SubscribeAsync]
    LocalNotify -->|Start 自动| SubAsync
    SubAsync -->|写入| Desired
    NotifyOpened[收到 NotifyOpened] -->|期望集命中则补订| SubAsync
    Unsubscribe[UnsubscribeAsync] -->|移出| Desired
  end

  subgraph serverSide [服务端 Peer]
    Subs[_subscriptions 订阅表]
    SubAsync -->|Sys subscribe 成功| Subs
    UnsubSys[Sys unsubscribe] --> Subs
  end

  subgraph broadcast [PipePeers]
    NotifyAll[Notify 全量广播]
    NotifySub[NotifySubscribers]
    NotifySub -->|IsSubscribed 过滤| Subs
    NotifyAll -->|不检查订阅| AllPeers[全部已连接 Peer]
  end
```

行为摘要：

| 操作 | 客户端期望集 | 服务端订阅表 |
|------|--------------|--------------|
| `SubscribeAsync` 成功 | 保留 | 加入 |
| `SubscribeAsync` 对端无方法 | 仍保留（便于补订） | 不变 |
| `UnsubscribeAsync` | 移除（不再自动补订） | 移除 |
| 对端 `NotifyClosed` | 保留 | 清除该方法 |
| 对端 `NotifyOpened` 且期望集命中 | 保留 | 自动再 Subscribe 后加入 |

`Peers.Notify` 发给快照中每一个 Peer；`Peers.NotifySubscribers` 仅发给 `IsSubscribed(method)` 为真的连接。
