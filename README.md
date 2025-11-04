# HttpClientExtensionsPack
A robust and production-ready extension library for HttpClient in .NET. It improves raw HttpClient usability with retry logic, JSON helpers, file handling, ETag caching, streaming, pagination, and convenience methods.

A high-utility extension toolkit for `HttpClient` in .NET. Designed for production workloads with retries, JSON helpers, streaming, file operations, caching, pagination, and RFC7807 problem handling.

A robust and production-ready extension library for `HttpClient` in .NET. It improves raw `HttpClient` usability with retry logic, JSON helpers, file handling, ETag caching, streaming, pagination, and convenience methods.

## Features

* ✅ JSON serialization and deserialization helpers
* ✅ Retry policy with exponential backoff and `Retry-After` support
* ✅ Detailed error handling (`HttpProblemException`)
* ✅ ETag and `If-None-Match` conditional GET support
* ✅ File upload and download utilities
* ✅ Chunked streaming download
* ✅ Pagination helper
* ✅ Timeout wrapper
* ✅ HEAD and OPTIONS helpers
* ✅ Request cloning for retry-safe execution
* ✅ Configurable tuned `HttpClient` factory

## Installation

Add the file `HttpClientExtensions.cs` to your project.

Planned: NuGet release

## Quick Start

```csharpcsharp
var client = HttpClientExtensions.CreateDefaultClient("https://api.example.com");
client.SetBearerToken("your_token_here");
```

### 2. GET JSON

```csharp
var user = await client.GetFromJsonSafeAsync<User>("/users/10");
```

### 3. POST JSON

```csharp
var created = await client.PostAsJsonSafeAsync<NewUser, User>("/users", new NewUser { Name = "Alex" });
```

### 4. File Download

```csharp
await client.DownloadToFileAsync("/files/report.pdf", "report.pdf");
```

### 5. File Upload

```csharp
using var fs = File.OpenRead("avatar.png");
await client.UploadFileAsync("/upload", fs, "avatar.png", contentType: "image/png");
```

### 6. Conditional GET (ETag)

```csharp
var result = await client.GetIfNoneMatchAsync<User>("/profile", currentEtag);
if (!result.NotModified)
{
    // Update cache
    cachedUser = result.Value;
    cachedEtag = result.ETag;
}
```

### 7. Stream Download

```csharp
await foreach (var chunk in client.StreamDownloadAsync("/bigfile.bin"))
{
    // Process chunk
}
```

### 8. Pagination

```csharp
await foreach (var post in client.PaginateAsync("/posts?page=1", FetchPage))
{
    Console.WriteLine(post);
}
```

### 9. Timeout Wrapper

```csharp
var result = await client.GetFromJsonSafeAsync<object>("/slow").WithTimeout(TimeSpan.FromSeconds(3));
```

## Error Handling

When an HTTP error occurs, a rich `HttpProblemException` is thrown containing:

* Status code
* Reason phrase
* Response body
* Content type
* Request URI
* HTTP method

## Design Goals

* No external dependencies
* Small, focused utility set
* Safe retry mechanism (cloned requests)
* Compatible with real production APIs
* JSON defaults aligned with REST best practices

## Supported .NET versions

* .NET 6+
* Recommended: .NET 8

## License

MIT License — Attribution required:

```
Created by Kshitij Jhangra
```
