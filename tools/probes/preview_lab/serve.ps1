<#
.SYNOPSIS
Serves a folder over http://127.0.0.1:<Port>/ and logs every request, for copying a BC-TEST web page in a browser and for
counting the requests a renderer makes.

.PARAMETER Root
The folder to serve.

.PARAMETER Port
The port on 127.0.0.1.

.PARAMETER Log
The request log (one line per request: time, method, path).
#>
param(
    [Parameter(Mandatory)] [string] $Root,
    [int] $Port = 8123,
    [string] $Log = (Join-Path $env:TEMP 'serve-requests.log')
)

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://127.0.0.1:$Port/")
$listener.Start()
"listening on http://127.0.0.1:$Port/ for $Root" | Out-File -FilePath $Log -Append -Encoding utf8
$types = @{ '.html' = 'text/html; charset=utf-8'; '.png' = 'image/png'; '.css' = 'text/css'; '.js' = 'text/javascript' }
while ($listener.IsListening) {
    $context = $listener.GetContext()
    $path = [Uri]::UnescapeDataString($context.Request.Url.AbsolutePath.TrimStart('/'))
    "{0:o} {1} /{2}" -f (Get-Date), $context.Request.HttpMethod, $path | Out-File -FilePath $Log -Append -Encoding utf8
    $file = Join-Path $Root $path
    $response = $context.Response
    if ($path -eq 'remote-pixel.png') {
        # A tiny picture that only the server has: whoever loads it shows up in the log.
        $file = Join-Path $Root 'bc-test-picture2.png'
    }

    if ($path -and (Test-Path -LiteralPath $file -PathType Leaf)) {
        $bytes = [System.IO.File]::ReadAllBytes($file)
        $extension = [System.IO.Path]::GetExtension($file).ToLowerInvariant()
        $response.ContentType = if ($types.ContainsKey($extension)) { $types[$extension] } else { 'application/octet-stream' }
        $response.OutputStream.Write($bytes, 0, $bytes.Length)
    }
    else {
        $response.StatusCode = 404
    }

    $response.Close()
}
