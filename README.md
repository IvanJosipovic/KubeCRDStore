# KubeCRDStore

[![Nuget](https://img.shields.io/nuget/vpre/KubeCRDStore.svg?style=flat-square)](https://www.nuget.org/packages/KubeCRDStore)
[![Nuget)](https://img.shields.io/nuget/dt/KubeCRDStore.svg?style=flat-square)](https://www.nuget.org/packages/KubeCRDStore)
[![codecov](https://codecov.io/gh/IvanJosipovic/KubeCRDStore/graph/badge.svg?token=Wu2ljeNY91)](https://codecov.io/gh/IvanJosipovic/KubeCRDStore)

A HTTP service that reads Kubernetes OpenAPI v3 documents using the local Kubernetes Context and serves CRD schemas as standalone JSON, rewriting local schema references so they can be consumed directly. This API is utilized by the `redhat.vscode-yaml` extension.

## How to use

- Install .Net 10
  - https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Install KubeCRDStore
  - `dotnet tool install --global KubeCRDStore --prerelease`
- Within the VS Code Project
  - Add `/.vscode/settings.json`
    ```json
    {
        "recommendations": [
            "redhat.vscode-yaml"
        ]
    }
    ```
  - Add `/.vscode/settings.json`
    ```json
    {
        "yaml.schemaStore.enable": false,
        "yaml.trace.server": "verbose",
        "vs-kubernetes": {
            "vs-kubernetes.crd-code-completion": "disabled"
        },
        "yaml.kubernetesCRDStore.url": "http://localhost:5000",
        "yaml.kubernetesCRDStore.enable": true,
        "yaml.schemas": {
          "kubernetes": ["*.yaml"]
        }
    }
    ```