# sas/ —— 上游 SimpleAnnotationServer 运行包（不入库）

本目录在仓库里是空的：`simpleAnnotationStore.war`（约 65 MB）和
`dependency\jetty-runner.jar`（约 9 MB）都是**第三方发布包**，不随仓库分发。

需要标注服务时，把这两个文件放回原位：

```text
sas/
├─ simpleAnnotationStore.war        # 必填
└─ dependency/
   └─ jetty-runner.jar              # 必填
```

获取方式：

| 文件 | 来源 |
|---|---|
| `simpleAnnotationStore.war` | <https://github.com/glenrobson/SimpleAnnotationServer/releases>（1.0.2，Apache-2.0） |
| `dependency\jetty-runner.jar` | Maven Central：`org.eclipse.jetty:jetty-runner:9.4.29.v20200521`（EPL-2.0 / Apache-2.0） |

放好后运行 `..\start-sas.ps1` 即可；`start-sas.ps1` 会给出缺文件的明确提示。
只使用图像服务（8182）时不需要本目录。
