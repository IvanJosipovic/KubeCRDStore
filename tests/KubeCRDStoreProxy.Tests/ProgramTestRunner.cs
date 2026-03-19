using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using k8s;
using Xunit;

namespace KubeCRDStoreProxy.Tests
{
    public class ProgramTests
    {
        [Fact]
        public void TestOpenApiIndexContainsBitnamiV1alpha1()
        {
            var fixtures = AppContext.BaseDirectory;
            var indexJson = File.ReadAllText(Path.Combine(fixtures, "1-openapiv3.json"));
            Assert.Contains("apis/bitnami.com/v1alpha1", indexJson);
        }

        [Fact]
        public void TestFindsSealedSecretSchema()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");

            var raw = Program.TryFindSchemaRaw(apiDoc, "bitnami.com", "SealedSecret", "v1alpha1", FixtureUri("2-bitnami-com-v1alpha1.json"));

            Assert.NotNull(raw);
        }

        [Fact]
        public void TestSchemaContainsApiVersionProperty()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");
            var raw = Program.TryFindSchemaRaw(apiDoc, "bitnami.com", "SealedSecret", "v1alpha1");

            Assert.NotNull(raw);
            Assert.Contains("\"apiVersion\"", raw);
        }

        [Fact]
        public void TestSchemaContainsKindProperty()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");
            var raw = Program.TryFindSchemaRaw(apiDoc, "bitnami.com", "SealedSecret", "v1alpha1");

            Assert.NotNull(raw);
            Assert.Contains("\"kind\"", raw);
        }

        [Fact]
        public void TestReturnsSchemaAsJsonObject()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");
            var raw = Program.TryFindSchemaRaw(apiDoc, "bitnami.com", "SealedSecret", "v1alpha1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.Equal(JsonValueKind.Object, actualDoc.RootElement.ValueKind);
        }

        [Fact]
        public void TestSchemaContainsPropertiesObject()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");
            var raw = Program.TryFindSchemaRaw(apiDoc, "bitnami.com", "SealedSecret", "v1alpha1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.True(actualDoc.RootElement.TryGetProperty("properties", out _));
        }

        [Fact]
        public void TestCollapsesSingleEntryAllOf()
        {
            var doc = ReadDocument("{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"test\",\"version\":\"1.0\"},\"components\":{\"schemas\":{\"MySchema\":{\"x-kubernetes-group-version-kind\":[{\"group\":\"example.com\",\"version\":\"v1\",\"kind\":\"Thing\"}],\"allOf\":[{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}]}}}}");

            var raw = Program.TryFindSchemaRaw(doc, "example.com", "Thing", "v1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.False(actualDoc.RootElement.TryGetProperty("allOf", out _));
        }

        [Fact]
        public void TestCollapsedAllOfRetainsChildSchemaContent()
        {
            var doc = ReadDocument("{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"test\",\"version\":\"1.0\"},\"components\":{\"schemas\":{\"MySchema\":{\"x-kubernetes-group-version-kind\":[{\"group\":\"example.com\",\"version\":\"v1\",\"kind\":\"Thing\"}],\"allOf\":[{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}]}}}}");

            var raw = Program.TryFindSchemaRaw(doc, "example.com", "Thing", "v1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.True(actualDoc.RootElement.GetProperty("properties").TryGetProperty("name", out _));
        }

        [Fact]
        public void TestCollapsesSingleEntryAnyOf()
        {
            var doc = ReadDocument("{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"test\",\"version\":\"1.0\"},\"components\":{\"schemas\":{\"MySchema\":{\"x-kubernetes-group-version-kind\":[{\"group\":\"example.com\",\"version\":\"v1\",\"kind\":\"Thing\"}],\"anyOf\":[{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}]}}}}");

            var raw = Program.TryFindSchemaRaw(doc, "example.com", "Thing", "v1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.False(actualDoc.RootElement.TryGetProperty("anyOf", out _));
        }

        [Fact]
        public void TestCollapsesSingleEntryOneOf()
        {
            var doc = ReadDocument("{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"test\",\"version\":\"1.0\"},\"components\":{\"schemas\":{\"MySchema\":{\"x-kubernetes-group-version-kind\":[{\"group\":\"example.com\",\"version\":\"v1\",\"kind\":\"Thing\"}],\"oneOf\":[{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}]}}}}");

            var raw = Program.TryFindSchemaRaw(doc, "example.com", "Thing", "v1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.False(actualDoc.RootElement.TryGetProperty("oneOf", out _));
        }

        [Fact]
        public void TestNotFound()
        {
            var doc = ReadDocument("{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"test\",\"version\":\"1.0\"},\"components\":{}}");
            var raw = Program.TryFindSchemaRaw(doc, "group", "Kind", "v1", FixtureUri("nonexistent.json"));
            Assert.Null(raw);
        }

        [Fact]
        public void TestFindsComponentSchema()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");

            var raw = Program.TryFindComponentSchemaRaw(apiDoc, "com.bitnami.v1alpha1.SealedSecret", FixtureUri("2-bitnami-com-v1alpha1.json"));

            Assert.NotNull(raw);
        }

        [Fact]
        public void TestComponentSchemaContainsApiVersionProperty()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");
            var raw = Program.TryFindComponentSchemaRaw(apiDoc, "com.bitnami.v1alpha1.SealedSecret", FixtureUri("2-bitnami-com-v1alpha1.json"));

            Assert.NotNull(raw);
            Assert.Contains("\"apiVersion\"", raw);
        }

        [Fact]
        public void TestReturnsNullForMissingComponentSchema()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");

            var raw = Program.TryFindComponentSchemaRaw(apiDoc, "missing.component.Schema", FixtureUri("2-bitnami-com-v1alpha1.json"));

            Assert.Null(raw);
        }

        [Fact]
        public void TestSchemaResponseIsSingleJsonDocument()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");

            var raw = Program.TryFindSchemaRaw(apiDoc, "bitnami.com", "SealedSecret", "v1alpha1", FixtureUri("2-bitnami-com-v1alpha1.json"));

            Assert.NotNull(raw);
            Assert.DoesNotContain("}{", raw);
        }

        [Fact]
        public void TestRewritesLocalRefsToProxyBaseUrl()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");

            var raw = Program.TryFindSchemaRaw(
                apiDoc,
                "bitnami.com",
                "SealedSecret",
                "v1alpha1",
                FixtureUri("2-bitnami-com-v1alpha1.json"),
                "http://localhost:5000");

            Assert.NotNull(raw);
            Assert.Contains("\"$ref\": \"http://localhost:5000/components/schemas/io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta\"", raw);
        }

        [Fact]
        public void TestCollapsesNestedSingleEntryAllOfWrappingRef()
        {
            var apiDoc = ReadFixtureDocument("2-bitnami-com-v1alpha1.json");

            var raw = Program.TryFindSchemaRaw(
                apiDoc,
                "bitnami.com",
                "SealedSecret",
                "v1alpha1",
                FixtureUri("2-bitnami-com-v1alpha1.json"),
                "http://localhost:5000");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.False(ContainsSingleEntryCompositionRefWrapper(actualDoc.RootElement));
        }

        [Fact]
        public void TestCollapsePreservesSiblingDescription()
        {
            var doc = ReadDocument("{\"openapi\":\"3.0.0\",\"info\":{\"title\":\"test\",\"version\":\"1.0\"},\"components\":{\"schemas\":{\"MySchema\":{\"x-kubernetes-group-version-kind\":[{\"group\":\"example.com\",\"version\":\"v1\",\"kind\":\"Thing\"}],\"allOf\":[{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}],\"description\":\"kept\"}}}}");

            var raw = Program.TryFindSchemaRaw(doc, "example.com", "Thing", "v1");

            Assert.NotNull(raw);
            using var actualDoc = JsonDocument.Parse(raw);
            Assert.Equal("kept", actualDoc.RootElement.GetProperty("description").GetString());
        }

        [Fact]
        public async Task TestHealthzEndpointReturnsOk()
        {
            await using var app = await CreateTestAppAsync();
            using var client = app.GetTestClient();

            using var response = await client.GetAsync("/healthz");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("ok", body);
        }

        [Fact]
        public async Task TestSchemaEndpointReturnsBadRequestForInvalidResourceName()
        {
            using var kubeClient = CreateDummyKubernetesClient();
            await using var app = await CreateTestAppAsync(kubeClient);
            using var client = app.GetTestClient();

            using var response = await client.GetAsync("/source.toolkit.fluxcd.io/not-valid.json");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("resource name must be in the format {ResourceKind}_{ResourceAPIVersion}", body);
        }

        [Fact]
        public async Task TestSchemaEndpointReturnsServerErrorWhenKubernetesClientIsUnavailable()
        {
            await using var app = await CreateTestAppAsync();
            using var client = app.GetTestClient();

            using var response = await client.GetAsync("/source.toolkit.fluxcd.io/gitrepository_v1.json");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("Kubernetes client configuration not available.", body);
        }

        [Fact]
        public async Task TestComponentSchemaEndpointReturnsServerErrorWhenKubernetesClientIsUnavailable()
        {
            await using var app = await CreateTestAppAsync();
            using var client = app.GetTestClient();

            using var response = await client.GetAsync("/components/schemas/io.k8s.apimachinery.pkg.apis.meta.v1.ObjectMeta");
            var body = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("Kubernetes client configuration not available.", body);
        }

        private static OpenApiDocument ReadDocument(string content)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
            var reader = new OpenApiJsonReader();
            var doc = reader.Read(ms, new Uri("https://example.test/openapi.json"), new OpenApiReaderSettings()).Document;
            Assert.NotNull(doc);
            return doc;
        }

        private static bool ContainsSingleEntryCompositionRefWrapper(JsonElement root)
        {
            var stack = new Stack<JsonElement>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();

                if (current.ValueKind == JsonValueKind.Object)
                {
                    foreach (var compositionKey in new[] { "allOf", "anyOf", "oneOf" })
                    {
                        if (current.TryGetProperty(compositionKey, out var composition)
                            && composition.ValueKind == JsonValueKind.Array
                            && composition.GetArrayLength() == 1)
                        {
                            var child = composition[0];
                            if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("$ref", out _))
                            {
                                return true;
                            }
                        }
                    }

                    foreach (var property in current.EnumerateObject())
                    {
                        stack.Push(property.Value);
                    }
                }
                else if (current.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in current.EnumerateArray())
                    {
                        stack.Push(item);
                    }
                }
            }

            return false;
        }

        private static Uri FixtureUri(string name) => new(Path.Combine(AppContext.BaseDirectory, name));

        private static OpenApiDocument ReadFixtureDocument(string name) => ReadDocument(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)));

        private static async Task<WebApplication> CreateTestAppAsync(Kubernetes? kubeClient = null)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();

            var app = builder.Build();
            Program.MapRoutes(app, kubeClient);
            await app.StartAsync();
            return app;
        }

        private static Kubernetes CreateDummyKubernetesClient()
            => new(new KubernetesClientConfiguration
            {
                Host = "http://127.0.0.1:65535"
            });
    }
}
