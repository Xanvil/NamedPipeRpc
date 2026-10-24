# 接口契约

契约的 Handler / Proxy 划分见 [NamedPipeRpc.Notify.struct.md](NamedPipeRpc.Notify.struct.md)。

`[RpcRequest]` 与 `[RpcNotify]` 由 Source Generator 生成 `Add*Handler`、`AddNotifyProxy`、`Get*Proxy`。管道上仍只认 RPC 方法名和 JSON 形状，两端不必共享同一个接口程序集。
