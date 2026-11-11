$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$wwwroot = Join-Path $root 'wwwroot'
if (-not (Test-Path -LiteralPath (Join-Path $root 'index.html')) -and (Test-Path -LiteralPath (Join-Path $wwwroot 'index.html'))) {
    $root = $wwwroot
}
$prefix = 'http://localhost:8088/'
$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add($prefix)
$shutdownRequested = $false
$mime = @{
    '.html'='text/html; charset=utf-8'; '.js'='application/javascript; charset=utf-8'
    '.css'='text/css; charset=utf-8'; '.json'='application/json; charset=utf-8'
    '.wasm'='application/wasm'; '.dll'='application/octet-stream'; '.dat'='application/octet-stream'
    '.png'='image/png'; '.jpg'='image/jpeg'; '.jpeg'='image/jpeg'; '.svg'='image/svg+xml'
    '.ico'='image/x-icon'; '.woff'='font/woff'; '.woff2'='font/woff2'; '.ttf'='font/ttf'
    '.txt'='text/plain; charset=utf-8'; '.webp'='image/webp'; '.webmanifest'='application/manifest+json'
}
try {
    if (-not (Test-Path -LiteralPath (Join-Path $root 'index.html'))) {
        throw "Non trovo index.html. Estrai tutti i file dello ZIP nella stessa cartella."
    }
    $listener.Start()
    Write-Host ''
    Write-Host 'REFERTO PALLAMANO - PROVA LOCALE' -ForegroundColor Green
    Write-Host "Applicazione: $prefix" -ForegroundColor Cyan
    Write-Host 'Per chiudere il server, premi CTRL+C.' -ForegroundColor Yellow
    Start-Process $prefix
    while ($listener.IsListening) {
        $context = $listener.GetContext()
        try {
            if ($context.Request.Url.AbsolutePath -eq '/__shutdown' -and $context.Request.HttpMethod -eq 'POST') {
                $shutdownRequested = $true
                $context.Response.StatusCode = 200
                $context.Response.ContentType = 'text/plain; charset=utf-8'
                $bytes = [Text.Encoding]::UTF8.GetBytes('Applicazione in chiusura.')
                $context.Response.ContentLength64 = $bytes.Length
                $context.Response.OutputStream.Write($bytes,0,$bytes.Length)
                $listener.Stop()
                continue
            }
            $relative = [Uri]::UnescapeDataString($context.Request.Url.AbsolutePath.TrimStart('/'))
            if ([string]::IsNullOrWhiteSpace($relative)) { $relative = 'index.html' }
            $candidate = [IO.Path]::GetFullPath((Join-Path $root $relative))
            $rootFull = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
            if (-not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
                $context.Response.StatusCode = 403
                $bytes = [Text.Encoding]::UTF8.GetBytes('Forbidden')
                $context.Response.ContentType = 'text/plain; charset=utf-8'
            } else {
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf) -and [IO.Path]::GetExtension($relative) -eq '') {
                    $candidate = Join-Path $root 'index.html'
                }
                if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
                    $context.Response.StatusCode = 404
                    $bytes = [Text.Encoding]::UTF8.GetBytes('Not found')
                    $context.Response.ContentType = 'text/plain; charset=utf-8'
                } else {
                    $bytes = [IO.File]::ReadAllBytes($candidate)
                    $ext = [IO.Path]::GetExtension($candidate).ToLowerInvariant()
                    if ($ext -eq '.br' -or $ext -eq '.gz') {
                        $innerExt = [IO.Path]::GetExtension([IO.Path]::GetFileNameWithoutExtension($candidate)).ToLowerInvariant()
                        $context.Response.ContentType = $mime[$innerExt]
                        if (-not $context.Response.ContentType) { $context.Response.ContentType = 'application/octet-stream' }
                        $context.Response.Headers.Add('Content-Encoding', $(if ($ext -eq '.br') { 'br' } else { 'gzip' }))
                        $context.Response.Headers.Add('Vary','Accept-Encoding')
                    } else {
                        $context.Response.ContentType = $mime[$ext]
                        if (-not $context.Response.ContentType) { $context.Response.ContentType = 'application/octet-stream' }
                    }
                    $context.Response.Headers.Add('Cache-Control','no-store')
                }
            }
            $context.Response.ContentLength64 = $bytes.Length
            try {
                $context.Response.OutputStream.Write($bytes,0,$bytes.Length)
            } catch [System.IO.IOException] {
                # Il browser ha interrotto la richiesta durante refresh o navigazione.
            } catch [System.Net.Sockets.SocketException] {
                # Connessione client chiusa durante l'invio della risposta.
            } catch [System.Net.HttpListenerException] {
                # Il client ha chiuso la connessione mentre il server inviava i dati.
            }
        } catch {
            Write-Host ("Richiesta non riuscita: " + $_.Exception.Message) -ForegroundColor Red
            try { $context.Response.StatusCode = 500 } catch {}
        } finally {
            try { $context.Response.OutputStream.Close() } catch {}
        }
    }
} catch {
    Write-Host ''
    Write-Host 'ERRORE DURANTE L''AVVIO:' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host 'La finestra restera aperta per permettere di leggere l''errore.' -ForegroundColor Yellow
} finally {
    if ($listener.IsListening) { $listener.Stop() }
    $listener.Close()
    if (-not $shutdownRequested) {
        Write-Host ''
        Read-Host 'Premi INVIO per chiudere questa finestra'
    }
}
