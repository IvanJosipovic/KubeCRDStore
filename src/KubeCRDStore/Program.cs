using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using k8s;

namespace KubeCRDStore;

public static class Program
{
    private const string MissingKubernetesClientMessage = "Kubernetes client configuration not available.";
    private const string InvalidResourceNameMessage = "resource name must be in the format {ResourceKind}_{ResourceAPIVersion}";
    private const string IntOrStringFormat = "int-or-string";
    private const string LocalSchemaReferencePrefix = "#/components/schemas/";
    private static readonly string[] CompositionKeys = ["allOf", "anyOf", "oneOf"];

    public static Task Main(string[] args) => BuildApp(args).RunAsync();

    public static WebApplication BuildApp(string[] args)
    {
        var app = WebApplication.CreateBuilder(args).Build();
        var kubeClient = CreateKubernetesClientFromKubeConfig(GetContext(args));
        MapRoutes(app, kubeClient);
        return app;
    }

    public static void MapRoutes(IEndpointRouteBuilder app, Kubernetes? kubeClient)
    {
        app.MapGet("/healthz", () => Results.Text("ok"));

        app.MapGet("/{group}/{name}.json", (HttpContext httpContext, string group, string name) =>
            HandleSchemaRequestAsync(kubeClient, httpContext, group, name));

        app.MapGet("/components/schemas/{name}", (HttpContext httpContext, string name) =>
            HandleComponentSchemaRequestAsync(kubeClient, httpContext, name));
    }

    public static Kubernetes? CreateKubernetesClientFromKubeConfig(string? context)
    {
        try
        {
            var config = KubernetesClientConfiguration.BuildConfigFromConfigFile((string?)null, context, null, false);
            return new Kubernetes(config);
        }
        catch
        {
            return null;
        }
    }

    public static string? TryFindSchemaRaw(OpenApiDocument document, string group, string kind, string version, Uri? documentUri = null, string? baseUrl = null)
    {
        EnsureWorkspace(document, documentUri);
        var found = FindMatchingSchema(document, group, kind, version);
        return found == null ? null : SerializeSchema(found, baseUrl);
    }

    public static string? TryFindComponentSchemaRaw(OpenApiDocument document, string name, Uri? documentUri = null, string? baseUrl = null)
    {
        EnsureWorkspace(document, documentUri);

        return document.Components?.Schemas != null && document.Components.Schemas.TryGetValue(name, out var schema)
            ? SerializeSchema(schema, baseUrl)
            : null;
    }

    private static string? GetContext(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--context" || args[i] == "-c") && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static async Task<HttpResponseMessage> SendRequestRawViaKubernetesClientAsync(Kubernetes kubeClient, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var type = kubeClient.GetType();
        var method = type.GetMethod("SendRequestRaw", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (method == null)
        {
            throw new InvalidOperationException("SendRequestRaw method not found on Kubernetes client");
        }

        var taskObj = method.Invoke(kubeClient, [null, request, cancellationToken]);
        if (taskObj is Task<HttpResponseMessage> typedTask)
        {
            return await typedTask.ConfigureAwait(false);
        }

        if (taskObj is Task genericTask)
        {
            await genericTask.ConfigureAwait(false);
            var resultProp = genericTask.GetType().GetProperty("Result");
            if (resultProp?.GetValue(genericTask) is HttpResponseMessage result)
            {
                return result;
            }
        }

        throw new InvalidOperationException("Unexpected return type from SendRequestRaw");
    }

    private static async Task<IResult> HandleSchemaRequestAsync(Kubernetes? kubeClient, HttpContext httpContext, string group, string name)
    {
        var resource = SplitResourceName(name);
        if (resource == null)
        {
            return Results.Text(InvalidResourceNameMessage, statusCode: 400);
        }

        var (resourceKind, resourceApiVersion) = resource.Value;

        return await HandleJsonResponseAsync(
            kubeClient,
            httpContext,
            (client, baseUrl, cancellationToken) => FindJsonInDocumentsAsync(
                client,
                BuildCandidateUris(client.BaseUri, group, resourceApiVersion),
                (document, documentUri) => TryFindSchemaRaw(document, group, resourceKind, resourceApiVersion, documentUri, baseUrl),
                cancellationToken),
            "schema not found in openapi v3 document");
    }

    private static Task<IResult> HandleComponentSchemaRequestAsync(Kubernetes? kubeClient, HttpContext httpContext, string name)
        => HandleJsonResponseAsync(
            kubeClient,
            httpContext,
            async (client, baseUrl, cancellationToken) => await FindJsonInDocumentsAsync(
                client,
                await BuildAllDocumentUrisAsync(client, cancellationToken),
                (document, documentUri) => TryFindComponentSchemaRaw(document, name, documentUri, baseUrl),
                cancellationToken),
            "schema not found in openapi v3 documents");

    private static async Task<OpenApiDocument?> FetchOpenApiDocumentAsync(Kubernetes kubeClient, Uri documentUri, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, documentUri);
        using var resp = await SendRequestRawViaKubernetesClientAsync(kubeClient, req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        memoryStream.Position = 0;

        var reader = new OpenApiJsonReader();
        var readResult = reader.Read(memoryStream, documentUri, new OpenApiReaderSettings());
        var document = readResult?.Document;
        if (document == null)
        {
            return null;
        }

        EnsureWorkspace(document, documentUri);
        return document;
    }

    private static async Task<IResult> HandleJsonResponseAsync(
        Kubernetes? kubeClient,
        HttpContext httpContext,
        Func<Kubernetes, string?, CancellationToken, Task<string?>> getJsonAsync,
        string notFoundMessage)
    {
        if (kubeClient == null)
        {
            return Results.Text(MissingKubernetesClientMessage, statusCode: 500);
        }

        try
        {
            var json = await getJsonAsync(kubeClient, GetBaseUrl(httpContext.Request), httpContext.RequestAborted);
            return json == null
                ? Results.Text(notFoundMessage, statusCode: 404)
                : Results.Text(json, "application/json");
        }
        catch (Exception ex)
        {
            return Results.Text(ex.Message, statusCode: 500);
        }
    }

    private static async Task<string?> FindJsonInDocumentsAsync(
        Kubernetes kubeClient,
        IEnumerable<Uri> candidateUris,
        Func<OpenApiDocument, Uri, string?> selectJson,
        CancellationToken cancellationToken)
    {
        foreach (var candidateUri in candidateUris)
        {
            var document = await FetchOpenApiDocumentAsync(kubeClient, candidateUri, cancellationToken);
            if (document == null)
            {
                continue;
            }

            var json = selectJson(document, candidateUri);
            if (json != null)
            {
                return json;
            }
        }

        return null;
    }

    private static async Task<List<Uri>> BuildAllDocumentUrisAsync(Kubernetes kubeClient, CancellationToken cancellationToken)
    {
        var result = new List<Uri>();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(kubeClient.BaseUri, "/openapi/v3"));
        using var resp = await SendRequestRawViaKubernetesClientAsync(kubeClient, req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return result;
        }

        using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (doc.RootElement.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var path in paths.EnumerateObject())
            {
                if (path.Value.TryGetProperty("serverRelativeURL", out var urlProp) && urlProp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(urlProp.GetString()))
                {
                    result.Add(new Uri(kubeClient.BaseUri, urlProp.GetString()!));
                }
            }
        }

        return result;
    }

    private static (string Kind, string Version)? SplitResourceName(string name)
    {
        var separatorIndex = name.LastIndexOf('_');
        return separatorIndex <= 0 || separatorIndex == name.Length - 1
            ? null
            : (name[..separatorIndex], name[(separatorIndex + 1)..]);
    }

    private static IEnumerable<Uri> BuildCandidateUris(Uri baseUri, string group, string version)
    {
        if (string.IsNullOrWhiteSpace(group) || string.Equals(group, "api", StringComparison.OrdinalIgnoreCase))
        {
            yield return new Uri(baseUri, $"/openapi/v3/api/{version}");
            yield break;
        }

        yield return new Uri(baseUri, $"/openapi/v3/apis/{group}/{version}");
        yield return new Uri(baseUri, $"/openapi/v3/api/{version}");
    }

    private static IOpenApiSchema? FindMatchingSchema(OpenApiDocument document, string group, string kind, string version)
    {
        var visited = new HashSet<IOpenApiSchema>();
        var queue = new Queue<IOpenApiSchema>();

        foreach (var schema in EnumerateSearchRootSchemas(document))
        {
            queue.Enqueue(schema);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            if (SchemaMatchesGvk(current, group, kind, version))
            {
                return current;
            }

            var resolved = ResolveSchemaReference(document, current);
            if (!ReferenceEquals(resolved, current))
            {
                queue.Enqueue(resolved);
            }

            foreach (var child in EnumerateChildSchemas(current))
            {
                queue.Enqueue(child);
            }
        }

        return null;
    }

    private static IEnumerable<IOpenApiSchema> EnumerateSearchRootSchemas(OpenApiDocument document)
    {
        if (document.Components?.Schemas != null)
        {
            foreach (var schema in document.Components.Schemas.Values)
            {
                yield return schema;
            }
        }

        if (document.Paths == null)
        {
            yield break;
        }

        foreach (var path in document.Paths.Values)
        {
            if (path.Operations == null)
            {
                continue;
            }

            foreach (var operation in path.Operations.Values)
            {
                if (operation.Parameters != null)
                {
                    foreach (var parameter in operation.Parameters)
                    {
                        if (parameter.Schema != null)
                        {
                            yield return parameter.Schema;
                        }
                    }
                }

                if (operation.RequestBody?.Content != null)
                {
                    foreach (var mediaType in operation.RequestBody.Content.Values)
                    {
                        if (mediaType.Schema != null)
                        {
                            yield return mediaType.Schema;
                        }
                    }
                }

                if (operation.Responses == null)
                {
                    continue;
                }

                foreach (var response in operation.Responses.Values)
                {
                    if (response?.Content == null)
                    {
                        continue;
                    }

                    foreach (var mediaType in response.Content.Values)
                    {
                        if (mediaType.Schema != null)
                        {
                            yield return mediaType.Schema;
                        }
                    }
                }
            }
        }
    }

    private static IOpenApiSchema ResolveSchemaReference(OpenApiDocument document, IOpenApiSchema schema)
    {
        if (schema is not OpenApiSchemaReference schemaReference)
        {
            return schema;
        }

        try
        {
            if (schemaReference.Target != null && !ReferenceEquals(schemaReference.Target, schemaReference))
            {
                return schemaReference.Target;
            }

            var reference = schemaReference.Reference;
            if (reference?.ExternalResource == null && !string.IsNullOrWhiteSpace(reference?.Id) && document.Components?.Schemas != null)
            {
                if (document.Components.Schemas.TryGetValue(reference.Id, out var componentSchema))
                {
                    return componentSchema;
                }
            }

            if (document.Workspace != null && !string.IsNullOrWhiteSpace(reference?.ReferenceV3))
            {
                return document.Workspace.ResolveReference<IOpenApiSchema>(reference.ReferenceV3) ?? schema;
            }

            return schema;
        }
        catch
        {
            return schema;
        }
    }

    private static bool SchemaMatchesGvk(IOpenApiSchema schema, string group, string kind, string version)
    {
        if (schema.Extensions == null || !schema.Extensions.TryGetValue("x-kubernetes-group-version-kind", out var extAny))
        {
            return false;
        }

        using var writer = new StringWriter();
        var jsonWriter = new OpenApiJsonWriter(writer, new OpenApiWriterSettings());
        extAny.Write(jsonWriter, OpenApiSpecVersion.OpenApi3_0);

        var node = JsonNode.Parse(writer.ToString());
        if (node is not JsonArray array)
        {
            return false;
        }

        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var gp = obj["group"]?.GetValue<string>();
            var ver = obj["version"]?.GetValue<string>();
            var kd = obj["kind"]?.GetValue<string>();

            if (string.Equals(gp ?? string.Empty, group ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ver ?? string.Empty, version ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                && string.Equals(kd ?? string.Empty, kind ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<IOpenApiSchema> EnumerateChildSchemas(IOpenApiSchema schema)
    {
        if (schema.Properties != null)
        {
            foreach (var child in schema.Properties.Values)
            {
                yield return child;
            }
        }

        if (schema.Items != null)
        {
            yield return schema.Items;
        }

        if (schema.AdditionalProperties != null)
        {
            yield return schema.AdditionalProperties;
        }

        if (schema.AllOf != null)
        {
            foreach (var child in schema.AllOf)
            {
                yield return child;
            }
        }

        if (schema.AnyOf != null)
        {
            foreach (var child in schema.AnyOf)
            {
                yield return child;
            }
        }

        if (schema.OneOf != null)
        {
            foreach (var child in schema.OneOf)
            {
                yield return child;
            }
        }

        if (schema.Not != null)
        {
            yield return schema.Not;
        }
    }

    private static string GetBaseUrl(HttpRequest request)
        => $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    private static void EnsureWorkspace(OpenApiDocument document, Uri? documentUri)
    {
        if (document.Workspace != null)
        {
            return;
        }

        var baseUri = documentUri ?? new Uri("https://in-memory.invalid/");
        var workspace = new OpenApiWorkspace(baseUri);
        if (documentUri != null)
        {
            workspace.AddDocumentId(documentUri.ToString(), documentUri);
        }

        workspace.RegisterComponents(document);
        document.Workspace = workspace;
    }

    private static string SerializeSchema(IOpenApiSchema schema, string? baseUrl)
    {
        using var writer = new StringWriter();
        schema.SerializeAsV3(new OpenApiJsonWriter(writer, new OpenApiWriterSettings()));
        return PostProcessSchemaJson(writer.ToString(), baseUrl);
    }

    private static string PostProcessSchemaJson(string json, string? baseUrl)
    {
        var root = JsonNode.Parse(json);
        if (root == null)
        {
            return json;
        }

        root = NormalizeSchema(root);

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            RewriteLocalSchemaReferences(root, baseUrl.TrimEnd('/'));
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode NormalizeSchema(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var propertyNames = new List<string>();
            foreach (var property in obj)
            {
                propertyNames.Add(property.Key);
            }

            foreach (var propertyName in propertyNames)
            {
                if (obj[propertyName] != null)
                {
                    obj[propertyName] = NormalizeSchema(obj[propertyName]!.DeepClone());
                }
            }

            if (IsIntOrStringSchema(obj))
            {
                return ExpandIntOrStringSchema(obj);
            }

            foreach (var compositionKey in CompositionKeys)
            {
                if (!obj.TryGetPropertyValue(compositionKey, out var compositionNode) || compositionNode is not JsonArray compositionArray || compositionArray.Count != 1)
                {
                    continue;
                }

                if (compositionArray[0] == null)
                {
                    continue;
                }

                var collapsedChild = compositionArray[0]!.DeepClone();
                CopySiblingProperties(obj, collapsedChild, compositionKey);
                return collapsedChild;
            }

            return obj;
        }

        if (node is JsonArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] != null)
                {
                    array[i] = NormalizeSchema(array[i]!.DeepClone());
                }
            }

            return array;
        }

        return node;
    }

    private static bool IsIntOrStringSchema(JsonObject obj)
    {
        if (string.Equals(obj["format"]?.GetValue<string>(), IntOrStringFormat, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return obj["x-kubernetes-int-or-string"] is JsonValue flag
            && flag.TryGetValue<bool>(out var enabled)
            && enabled;
    }

    private static JsonObject ExpandIntOrStringSchema(JsonObject source)
    {
        var replacement = new JsonObject
        {
            ["oneOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject { ["type"] = "integer" })
        };

        CopySiblingProperties(source, replacement, "format", "type", "x-kubernetes-int-or-string");
        return replacement;
    }

    private static void CopySiblingProperties(JsonObject source, JsonNode target, params string[] skippedProperties)
    {
        if (target is not JsonObject targetObject)
        {
            return;
        }

        foreach (var property in source)
        {
            if (Array.IndexOf(skippedProperties, property.Key) < 0 && !targetObject.ContainsKey(property.Key))
            {
                targetObject[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static void RewriteLocalSchemaReferences(JsonNode node, string baseUrl)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("$ref", out var refNode) && refNode is JsonValue refValue)
            {
                var refText = refValue.GetValue<string>();
                if (refText.StartsWith(LocalSchemaReferencePrefix, StringComparison.Ordinal))
                {
                    obj["$ref"] = $"{baseUrl}/components/schemas/{refText[LocalSchemaReferencePrefix.Length..]}";
                }
            }

            foreach (var property in obj)
            {
                if (property.Value != null)
                {
                    RewriteLocalSchemaReferences(property.Value, baseUrl);
                }
            }

            return;
        }

        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item != null)
                {
                    RewriteLocalSchemaReferences(item, baseUrl);
                }
            }
        }
    }
}
