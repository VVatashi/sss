# SimpleShadowsocks

SimpleShadowsocks - это компактный Shadowsocks-подобный стек на .NET 9 для личного туннеля и тестовых окружений. В репозитории есть:

- `SimpleShadowsocks.Server` - CLI tunnel endpoint для VPS/сервера;
- `SimpleShadowsocks.Client` - локальный CLI SOCKS5/HTTP-клиент с routing rules, failover, HTTP reverse proxy agent и Windows full-tunnel helper;
- `SimpleShadowsocks.Client.Maui` - Android UI/VPN-клиент на .NET MAUI.

Проект поддерживает:

- туннелирование TCP и UDP;
- локальный SOCKS5 `CONNECT` и `UDP ASSOCIATE`;
- опциональный локальный HTTP forward proxy для `http://` через tunnel;
- опциональный локальный HTTP reverse proxy через серверный listener и клиентский allowlist;
- несколько upstream tunnel-серверов с `round-robin`/failover;
- split-tunnel через `TrafficRoutingRules`;
- локальную SOCKS5-аутентификацию `USERNAME/PASSWORD`;
- шифрование `ChaCha20-Poly1305`, `AES-256-GCM`, `AEGIS-128L`, `AEGIS-256`;
- сжатие `Deflate`, `Gzip`, `Brotli` для протоколов `v2`, `v3` и `v4`;
- Android VPN-клиент и Windows system-wide tunnel helper на базе `hev-socks5-tunnel`.

## Содержание

- [Для пользователя](#для-пользователя)
  - [Что это и когда использовать](#что-это-и-когда-использовать)
  - [Состав приложений](#состав-приложений)
  - [Готовые релизы и бинарники](#готовые-релизы-и-бинарники)
  - [Быстрый старт](#быстрый-старт)
  - [Запуск](#запуск)
  - [Проверка работы](#проверка-работы)
- [Для разработчика](#для-разработчика)
  - [Технические требования](#технические-требования)
  - [Сборка из исходников](#сборка-из-исходников)
  - [Публикация release-артефактов](#публикация-release-артефактов)
  - [Тестирование](#тестирование)
  - [Архитектура решения](#архитектура-решения)
  - [Логирование](#логирование)
  - [Детальный разбор протокола](#детальный-разбор-протокола)
  - [Ключевые архитектурные решения](#ключевые-архитектурные-решения)
  - [Ограничения текущей версии](#ограничения-текущей-версии)

## Для пользователя

### Что это и когда использовать

SimpleShadowsocks поднимает защищённый туннель между клиентом и сервером:

1. на сервере запускается tunnel endpoint;
2. на клиенте поднимается локальный SOCKS5-прокси и, при необходимости, HTTP forward/reverse proxy runtime;
3. приложения отправляют трафик в один из этих локальных прокси;
4. трафик уходит к вашему серверу в зашифрованном виде.

Проект подходит, если нужен:

- простой self-hosted туннель без сложной панели управления;
- локальный SOCKS5 или HTTP proxy для браузера, системных клиентов и тестовых приложений;
- split-tunnel по доменам и подсетям;
- Android VPN-клиент или Windows full-tunnel поверх локального SOCKS5.

### Состав приложений

#### 1. `SimpleShadowsocks.Server`

CLI-сервер, который:

- принимает туннельные подключения клиентов;
- поддерживает протоколы `v1`, `v2`, `v3` и `v4`;
- проксирует TCP и UDP в upstream;
- может дополнительно поднять локальный HTTP reverse proxy listener;
- ограничивает количество туннелей и сессий политиками сервера.

#### 2. `SimpleShadowsocks.Client`

CLI-клиент, который:

- поднимает локальный SOCKS5-сервер;
- может дополнительно поднять локальный HTTP proxy;
- может держать постоянный туннель для server-initiated HTTP reverse proxy;
- умеет работать как с одним `RemoteHost`, так и с набором `RemoteServers`;
- применяет `TrafficRoutingRules` для SOCKS5 `CONNECT`, `UDP ASSOCIATE` и HTTP target host;
- поддерживает локальную SOCKS5-аутентификацию;
- для HTTP proxy использует server-side `HttpClient` и не добавляет `Via`/`Forwarded`/`X-Forwarded-*`.
- для HTTP reverse proxy матчится по allowlist (`Host` и/или `PathPrefix`) и тоже не добавляет `Via`/`Forwarded`/`X-Forwarded-*`.
- на Windows поставляется вместе с `run-full-tunnel.ps1`, `wintun.dll` и `hev-socks5-tunnel.exe`.

#### 3. `SimpleShadowsocks.Client.Maui`

Android-приложение, которое:

- предоставляет UI для настройки туннеля;
- запускает локальный SOCKS5 runtime внутри приложения;
- поднимает `VpnService`;
- использует встроенный `hev-socks5-tunnel` как native library;
- маршрутизирует TCP, UDP и DNS через Android VPN pipeline.

### Готовые релизы и бинарники

Актуально на 3 апреля 2026 года.

- Последний релиз: [`v0.3.0`](https://github.com/VVatashi/sss/releases/tag/v0.3.0)
- Дата публикации: `2026-04-03 10:13:27 UTC`
- Основные изменения релиза:
  - `Add system-wide VPN for Windows`
  - `Add split tunnel`
  - `Add SOCKS5 auth`
- Предыдущие релизы:
  - [`v0.2.0`](https://github.com/VVatashi/sss/releases/tag/v0.2.0)
  - [`v0.1.0`](https://github.com/VVatashi/sss/releases/tag/v0.1.0)

Артефакты релиза `v0.3.0`:

- Сервер:
  - [`server-win-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/server-win-x64.zip)
  - [`server-linux-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/server-linux-x64.zip)
  - [`server-osx-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/server-osx-x64.zip)
  - [`server-osx-arm64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/server-osx-arm64.zip)
- CLI-клиент:
  - [`client-win-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/client-win-x64.zip)
  - [`client-linux-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/client-linux-x64.zip)
  - [`client-osx-x64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/client-osx-x64.zip)
  - [`client-osx-arm64.zip`](https://github.com/VVatashi/sss/releases/download/v0.3.0/client-osx-arm64.zip)
- Android:
  - [`com.simpleshadowsocks.client.maui-Signed.apk`](https://github.com/VVatashi/sss/releases/download/v0.3.0/com.simpleshadowsocks.client.maui-Signed.apk)

### Быстрый старт

#### 1. Сервер

1. Скачайте `server-<platform>.zip`.
2. Распакуйте архив на сервере.
3. Создайте или обновите `appsettings.json` рядом с `SimpleShadowsocks.Server.dll`.

Пример:

```json
{
  "ListenPort": 8388,
  "SharedKey": "your-strong-key",
  "HandshakeMaxClockSkewSeconds": 60,
  "ReplayWindowSeconds": 300,
  "MaxConcurrentTunnels": 1024,
  "MaxSessionsPerTunnel": 1024,
  "ConnectTimeoutMs": 10000,
  "HttpReverseProxy": {
    "Enabled": false,
    "ListenPort": 8081,
    "ListenAddress": "127.0.0.1"
  }
}
```

`SharedKey` на клиенте и сервере должен совпадать.

Поддерживаемые форматы `SharedKey`:

- `hex:<64 hex символа>` - ровно 32 байта;
- `base64:<...>` - ровно 32 байта после декодирования;
- обычная строка - из неё вычисляется `SHA-256`.

#### 2. CLI-клиент

1. Скачайте `client-<platform>.zip`.
2. Распакуйте архив локально.
3. Настройте `appsettings.json`.

Базовый пример:

```json
{
  "ListenPort": 1080,
  "ListenAddress": "127.0.0.1",
  "RemoteHost": "your-server-host",
  "RemotePort": 8388,
  "RemoteServers": [],
  "SharedKey": "your-strong-key",
  "HandshakeMaxClockSkewSeconds": 60,
  "ReplayWindowSeconds": 300,
  "HeartbeatIntervalSeconds": 10,
  "IdleTimeoutSeconds": 45,
  "ReconnectBaseDelayMs": 200,
  "ReconnectMaxDelayMs": 2000,
  "ReconnectMaxAttempts": 12,
  "MaxConcurrentSessions": 1024,
  "SessionReceiveChannelCapacity": 256,
  "ProtocolVersion": 3,
  "EnableCompression": false,
  "CompressionAlgorithm": "Deflate",
  "TunnelCipherAlgorithm": "ChaCha20Poly1305",
  "HttpProxy": {
    "Enabled": false,
    "ListenPort": 8080,
    "ListenAddress": "127.0.0.1"
  },
  "HttpReverseProxy": {
    "Enabled": false,
    "Routes": [
      {
        "Host": "app.local",
        "PathPrefix": "/",
        "TargetBaseUrl": "http://127.0.0.1:5000/",
        "StripPathPrefix": false
      }
    ]
  },
  "Socks5Authentication": {
    "Enabled": false,
    "Username": "",
    "Password": ""
  },
  "TrafficRoutingRules": [
    {
      "Match": "*",
      "Decision": "Tunnel"
    }
  ]
}
```

Что важно:

- `ListenAddress` задаёт локальный адрес SOCKS5-сервера;
- `HttpProxy.Enabled=true` поднимает локальный HTTP forward proxy на `HttpProxy.ListenAddress:HttpProxy.ListenPort`;
- `HttpReverseProxy.Enabled=true` включает приём server-initiated HTTP reverse proxy запросов на клиенте;
- `HttpReverseProxy.Routes` задаёт allowlist-правила: первое совпадение по `Host` и/или `PathPrefix` побеждает;
- `HttpReverseProxy.Routes[*].TargetBaseUrl` должен быть абсолютным `http://` URL локального приложения на клиенте;
- `HttpReverseProxy.Routes[*].StripPathPrefix=true` удаляет `PathPrefix` из входящего пути перед отправкой в локальный origin;
- `RemoteServers` используется вместо `RemoteHost`/`RemotePort`, если список не пустой;
- `TrafficRoutingRules` применяются сверху вниз, первое совпавшее правило побеждает;
- если `TrafficRoutingRules` отсутствуют, клиент автоматически использует `* -> Tunnel`;
- `Socks5Authentication.Enabled=true` включает `USERNAME/PASSWORD` для входящих клиентов SOCKS5;
- HTTP proxy поддерживает только обычные `http://` absolute-form запросы;
- `CONNECT` и `https://` в HTTP proxy сейчас возвращают `501 Not Implemented`; для HTTPS используйте локальный SOCKS5 proxy;
- long-lived HTTP streaming (`SSE`, chunked/no-`Content-Length` response) поддерживается и в `Direct`, и в `Tunnel` режиме;
- WebSocket upgrade через HTTP forward/reverse proxy сейчас явно отклоняется с `501 WebSocket Not Supported`;
- HTTP proxy применяет те же `TrafficRoutingRules`, что и SOCKS5: `Tunnel` идёт через server-side `HttpClient`, `Direct` выполняется локально, `Drop` возвращает `403 Forbidden`;
- поддерживаемые `TunnelCipherAlgorithm`: `ChaCha20Poly1305`, `Aes256Gcm`, `Aegis128L`, `Aegis256`;
- поддерживаемые `CompressionAlgorithm`: `Deflate`, `Gzip`, `Brotli`.

Примеры routing rules:

- все через туннель:

```json
[
  { "Match": "*", "Decision": "Tunnel" }
]
```

- локальные сети напрямую, остальное в туннель:

```json
[
  { "Match": "10.0.0.0/8", "Decision": "Direct" },
  { "Match": "192.168.0.0/16", "Decision": "Direct" },
  { "Match": "*.corp.example", "Decision": "Direct" },
  { "Match": "*", "Decision": "Tunnel" }
]
```

- блокировка части трафика:

```json
[
  { "Match": "*.ads.example", "Decision": "Drop" },
  { "Match": "*", "Decision": "Tunnel" }
]
```

Поведение правил:

- `Decision: "Tunnel"` - отправить через tunnel backend;
- `Decision: "Direct"` - отправить напрямую;
- `Decision: "Drop"` - отклонить TCP `CONNECT` и отбросить UDP datagram;
- host/domain-правила поддерживают точный host и suffix-маски `*.example.com` или `.example.com`;
- для IP-правил используйте CIDR, например `10.0.0.0/8` или `fd00::/8`.

#### 3. Android-клиент

1. Установите APK из релиза.
2. При первом запуске заполните:
   - `Local SOCKS5 Port`
   - `Tunnel Host`
   - `Tunnel Port`
   - `Shared Key`
   - `Traffic Routing Rules`
   - `Enable SOCKS5 Authentication`
   - `SOCKS5 Username` / `SOCKS5 Password`
   - `Cipher Algorithm`
   - `Enable Compression` / `Compression Algorithm`
3. Нажмите `Start`.
4. Подтвердите системный VPN-диалог Android.

Особенности Android:

- встроенный `hev-socks5-tunnel` загружается как native library из APK;
- при включённой SOCKS5-аутентификации Android VPN использует те же `Username` и `Password` для доступа к локальному SOCKS5 runtime;
- поле `Logs` хранит только runtime-буфер последних `512` сообщений;
- для текущей Android VPN-реализации `Private DNS` должен быть отключён, иначе `DNS-over-TLS` на `:853` не проходит через pipeline.

### Запуск

#### Сервер

```powershell
SimpleShadowsocks.Server.exe [listenPort] [sharedKey]
```

#### CLI-клиент

```powershell
SimpleShadowsocks.Client.exe [listenPort] [remoteHost] [remotePort] [sharedKey]
```

Если заданы CLI-аргументы `remoteHost`, `remotePort`, они переопределяют одиночный remote endpoint из `appsettings.json`.

Для HTTP proxy укажите в приложении `127.0.0.1:8080` как обычный HTTP proxy и включите `HttpProxy.Enabled=true` в `appsettings.json`.

Для HTTP reverse proxy включите `HttpReverseProxy.Enabled=true` в `appsettings.json` клиента, `HttpReverseProxy.Enabled=true` в `appsettings.json` сервера и отправляйте запросы в локальный listener сервера, например `http://127.0.0.1:8081/...`.

Пример reverse-route на клиенте:

```json
{
  "HttpReverseProxy": {
    "Enabled": true,
    "Routes": [
      {
        "Host": "app.local",
        "TargetBaseUrl": "http://127.0.0.1:5000/"
      },
      {
        "PathPrefix": "/api",
        "TargetBaseUrl": "http://127.0.0.1:7001/backend",
        "StripPathPrefix": true
      }
    ]
  }
}
```

Поведение reverse proxy:

- сервер принимает обычный локальный HTTP/1.1 request и пересылает его в клиента по туннелю;
- клиент выбирает локальный origin по `Host` и/или `PathPrefix`;
- long-lived HTTP streaming и `SSE` поддерживаются; для очень длинных idle-period origin лучше отправлять периодические heartbeat-комментарии (`: ping\n\n`);
- активный long-lived HTTP stream не мигрирует через reconnect туннеля: текущее соединение завершается, клиент/приложение должны открыть его заново;
- WebSocket upgrade (`101 Switching Protocols`) не поддерживается и отклоняется на входе;
- серверный reverse-listener логирует `rawTarget`, `decodedTarget`, `parsedPath`, `hasQuery`, `queryLength`, `queryPreview`, `hostHeader`, `scheme` и `authority`, чтобы длинные query вроде SSE token не терялись в обрезанном логе;
- если маршрут не найден, клиент возвращает `404 Not Found`;
- если на клиенте reverse proxy не включён, сервер получит `403 Forbidden`;
- для origin-form request-target серверный reverse-listener сначала делает до двух проходов URL decode, затем явно разделяет `path` и `query` по первому реальному `?` и только после этого формирует canonical `PathAndQuery` для туннеля;
- текущая реализация рассчитана на один активный reverse-proxy клиент на сервер.

#### Windows: system-wide tunnel через Wintun

Windows-сборка `SimpleShadowsocks.Client` автоматически включает:

- `hev-socks5-tunnel.exe`
- `wintun.dll`
- `msys-2.0.dll`
- `run-full-tunnel.ps1`
- `hev-socks5-tunnel.template.yml`

Запускать helper нужно из каталога клиента с правами администратора:

```powershell
.\run-full-tunnel.ps1
```

Что делает скрипт:

- читает `appsettings.json`;
- переносит SOCKS5-аутентификацию в итоговый `hev-socks5-tunnel.yml`, если она включена;
- поднимает `hev-socks5-tunnel` поверх локального SOCKS5;
- создаёт bypass-маршруты для `RemoteHost` или списка `RemoteServers`;
- корректно обрабатывает `RemoteHost` и `RemoteServers` даже если upstream endpoint только один;
- отправляет остальной IPv4/IPv6 трафик через `Wintun`;
- при ошибке, `Ctrl+C` или штатном завершении удаляет маршруты и останавливает helper-процесс.

Для full-tunnel рекомендуется оставлять `ListenAddress` равным `127.0.0.1`.

#### Linux: systemd unit для сервера

```ini
[Unit]
Description=SimpleShadowsocks Tunnel Server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=nobody
Group=nogroup
WorkingDirectory=/opt/simple-shadowsocks/server
ExecStart=/usr/bin/dotnet /opt/simple-shadowsocks/server/SimpleShadowsocks.Server.dll
Restart=always
RestartSec=2
Environment=DOTNET_ENVIRONMENT=Production
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
```

### Проверка работы

1. Запустите сервер.
2. Запустите клиент.
3. Укажите в приложении или браузере SOCKS5-прокси `127.0.0.1:1080` или HTTP proxy `127.0.0.1:8080`.
4. Проверьте, что трафик действительно маршрутизируется через ожидаемое правило `TrafficRoutingRules`.

## Для разработчика

### Технические требования

- `.NET SDK 9.x` или `10.x` - `global.json` разрешает локальный roll-forward на установленный SDK этих major-версий;
- `maui-android` workload для Android-сборки;
- подготовленное Android-окружение для локального `dotnet publish` MAUI;
- JDK/Android SDK, совместимые с вашей локальной MAUI toolchain;
- сборка решения выполняется последовательно, без параллельной компиляции проектов.

На уровне репозитория:

- общий output вынесен в `bin/<ProjectName>/...`;
- intermediates вынесены в `obj/<ProjectName>/...`;
- `BuildInParallel=false` в `Directory.Build.props`.

Все команды предполагают запуск из корня репозитория.

### Сборка из исходников

Рекомендуемая последовательность:

```powershell
# Библиотеки
dotnet build src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj
dotnet build src\SimpleShadowsocks.Client.Core\SimpleShadowsocks.Client.Core.csproj
dotnet build src\SimpleShadowsocks.Server.Core\SimpleShadowsocks.Server.Core.csproj

# Приложения
dotnet build src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj
dotnet build src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj

# Тестовый проект
dotnet build tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

MAUI/Android локально не стоит собирать в произвольном окружении. Для него нужен рабочий Android toolchain на вашей машине или отдельная CI/build-машина.

### Публикация release-артефактов

Ручной publish desktop-бинарников:

```powershell
# Windows x64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r win-x64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r win-x64 --self-contained false

# Linux x64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r linux-x64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r linux-x64 --self-contained false

# macOS x64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r osx-x64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r osx-x64 --self-contained false

# macOS ARM64
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r osx-arm64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r osx-arm64 --self-contained false
```

Ручной publish Android APK:

```powershell
dotnet publish src\SimpleShadowsocks.Client.Maui\SimpleShadowsocks.Client.Maui.csproj -c Release -f net9.0-android
```

Корневой скрипт для локальной упаковки всех release-артефактов:

```powershell
pwsh .\publish-all.ps1
```

Что делает `publish-all.ps1`:

- последовательно собирает общие библиотеки;
- публикует `server` и `client` для `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`;
- исключает `.pdb` из архивов;
- сохраняет ZIP-артефакты в `artifacts\publish`;
- собирает Android тем же `dotnet publish`, что и рабочая ручная команда;
- копирует только top-level signed APK из `bin\SimpleShadowsocks.Client.Maui\Release\net9.0-android`.

Итоговые файлы:

- `artifacts/publish/server-win-x64.zip`
- `artifacts/publish/server-linux-x64.zip`
- `artifacts/publish/server-osx-x64.zip`
- `artifacts/publish/server-osx-arm64.zip`
- `artifacts/publish/client-win-x64.zip`
- `artifacts/publish/client-linux-x64.zip`
- `artifacts/publish/client-osx-x64.zip`
- `artifacts/publish/client-osx-arm64.zip`
- `artifacts/publish/com.simpleshadowsocks.client.maui-Signed.apk`

Основные publish-output директории:

- `bin/SimpleShadowsocks.Server/Release/net9.0/<RID>/publish`
- `bin/SimpleShadowsocks.Client/Release/net9.0/<RID>/publish`
- `bin/SimpleShadowsocks.Client.Maui/Release/net9.0-android`

### Тестирование

Базовый прогон:

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

Категории тестов:

- `Unit`
- `Integration`
- `Performance`

Рекомендуемый последовательный запуск:

```powershell
pwsh .\tests\run-tests.ps1
```

По умолчанию `tests\run-tests.ps1` запускает только `Unit` и `Integration`. Performance-тесты нужно включать явно:

```powershell
pwsh .\tests\run-tests.ps1 -IncludePerformance
```

Ручной запуск по категориям:

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Unit"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Integration"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Performance"
```

Целевой short perf-smoke для hot path без полного matrix:

```powershell
$env:SS_PERF_TOTAL_MB='16'
$env:SS_PERF_CHUNK_KB='64'
$env:SS_PERF_STREAMS='2'
$env:SS_PERF_PAYLOAD_VARIANTS='64'
$env:SS_PERF_CIPHERS='ChaCha20Poly1305'
$env:SS_PERF_COMPRESSIONS='off'
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "FullyQualifiedName~PerformanceMeasurementsTests.Measure_Throughput_And_Allocations_Matrix_MixedNoisePayload"
```

Что покрыто тестами:

- SOCKS5 `CONNECT` в standalone и tunnel-режимах;
- HTTP proxy в `Direct` и `Tunnel` режимах;
- HTTP reverse proxy server -> client -> local origin для `GET` и `POST`;
- allowlist routing reverse proxy по `Host` и `PathPrefix`;
- `POST`/body relay и отсутствие disclosure-заголовков (`Via`, `Forwarded`, `X-Forwarded-*`) на origin;
- `403` при выключенном client reverse proxy и `404` при отсутствии matching reverse-route;
- явные `501` для HTTP `CONNECT` и `https://` absolute-form;
- long-lived chunked/SSE stream через HTTP forward и reverse proxy без premature timeout;
- cleanup HTTP/reverse-HTTP session после downstream disconnect и tunnel fault;
- явный `501 WebSocket Not Supported` для HTTP forward и reverse proxy;
- `UDP ASSOCIATE`, relay и routing direct/tunnel по destination;
- аутентификация `USERNAME/PASSWORD` для локального SOCKS5;
- failover на следующий tunnel-сервер при отказе `CONNECT`;
- reconnect и миграция активных session после разрыва туннеля;
- одновременная передача через SOCKS5, HTTP forward proxy и HTTP reverse proxy без взаимных конфликтов;
- разные AEAD-алгоритмы и компрессия `v2`/`v3`/`v4`;
- span/memory-based сериализация protocol payload и zero-copy UDP parse из `ReadOnlyMemory<byte>`;
- replay/ack lifecycle для pending session-кадров без повторных копий payload при reconnect/recovery;
- regression-проверки, что tracked pending payload изолирован от переиспользуемых source/read buffer;
- performance matrix изолирует каждый cipher/compression mode на fresh tunnel runtime, чтобы не переносить state между измерениями;
- benchmark-сценарии для UDP performance.

Полезные переменные окружения для performance-тестов:

- `SS_PERF_TOTAL_MB` - по умолчанию `128`
- `SS_PERF_CHUNK_KB` - по умолчанию `64`
- `SS_PERF_STREAMS` - по умолчанию `4`
- `SS_PERF_PAYLOAD_VARIANTS` - по умолчанию `256`
- `SS_PERF_WARMUP_TIMEOUT_SEC` - по умолчанию `90`
- `SS_PERF_MEASUREMENT_TIMEOUT_SEC` - по умолчанию `600`

### Архитектура решения

Слои решения:

1. `SimpleShadowsocks.Protocol`
   - wire-модели, framing, handshake, AEAD, compression.
2. `SimpleShadowsocks.Client.Core`
   - SOCKS5 runtime, HTTP forward/reverse proxy runtime, routing rules, tunnel client, reconnect/session migration.
3. `SimpleShadowsocks.Server.Core`
   - tunnel server, handshake, session lifecycle, upstream TCP/UDP proxying, server-side HTTP relay и локальный reverse proxy listener.
4. Хосты:
   - `SimpleShadowsocks.Client`
   - `SimpleShadowsocks.Server`
   - `SimpleShadowsocks.Client.Maui`

Внутренние streaming-path API для TCP и HTTP теперь используют `OwnedPayloadChunk` вместо сырых `byte[]` в `ChannelReader`. Это нужно для leased/pool-aware обработки payload без лишних копий. Любой consumer такого reader должен вызывать `Dispose()` на каждом chunk сразу после записи/обработки.

Ключевые директории:

- `src/SimpleShadowsocks.Protocol`
- `src/SimpleShadowsocks.Client.Core/Socks5`
- `src/SimpleShadowsocks.Client.Core/Tunnel`
- `src/SimpleShadowsocks.Server.Core/Tunnel`
- `src/SimpleShadowsocks.Client/Platforms/Windows`
- `src/SimpleShadowsocks.Client.Maui/Platforms/Android`
- `tests/SimpleShadowsocks.Client.Tests`

### Логирование

Логирование унифицировано для `Client`, `Server`, `Client.Core`, `Server.Core`:

- timestamp в UTC;
- уровень `INFO/WARN/ERROR`;
- компонент;
- протокол;
- идентификатор session, если применимо;
- сообщение и поля ошибки при исключениях.

Пример:

```text
2026-03-16T19:10:42.1234567+00:00 level=INFO component=socks5-server protocol=TUNNEL/UDP session=42 msg="tunnel udp session opened"
```

Политика логирования:

- логируются lifecycle-события и ошибки;
- нет логирования каждого пакета в hot-path;
- Android UI хранит последние `512` записей в кольцевом буфере.

### Детальный разбор протокола

Поддерживаются три версии framing.

`v1`:

```text
+--------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-------------+-----------+--------------+
```

`v2`:

```text
+--------+----------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | FLAGS(1) | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+----------+------------+-------------+-----------+--------------+
```

`v3` использует тот же заголовок, что и `v2`, но добавляет HTTP relay в обе стороны туннеля.

`v4` сохраняет формат заголовка `v3`, но добавляет `Ack`/`Recover` для selective retransmit и replay неподтверждённых session-кадров после reconnect:

- `HttpRequest(8)` - старт HTTP-сессии с методом, target URI, HTTP-version и header list;
- `HttpRequestEnd(9)` - конец request body;
- `HttpResponse(10)` - status line и response headers;
- `ReverseHttpRequest(11)` - server-initiated HTTP request в клиента;
- `ReverseHttpRequestEnd(12)` - конец request body для reverse-сессии;
- `ReverseHttpResponse(13)` - ответ клиента на server-initiated HTTP request;
- `Data(2)` продолжает использоваться для request/response body.

Поля:

- `VER` - версия кадра (`1`, `2`, `3` или `4`);
- `TYPE` - `Connect(1)`, `Data(2)`, `Close(3)`, `Ping(4)`, `Pong(5)`, `UdpAssociate(6)`, `UdpData(7)`, `HttpRequest(8)`, `HttpRequestEnd(9)`, `HttpResponse(10)`, `ReverseHttpRequest(11)`, `ReverseHttpRequestEnd(12)`, `ReverseHttpResponse(13)`, `Ack(14)`, `Recover(15)`;
- `FLAGS` в `v2`/`v3`/`v4`:
  - `0x01` - `PayloadCompressed`
  - `0x02` - `CompressionEnabled`
  - `0x0C` - `CompressionAlgorithmMask`
- `SESSION` - идентификатор логической сессии;
- `SEQUENCE` - монотонный счётчик кадров;
- `LEN` - длина payload.

Правила:

- максимальный payload - `1 MiB`;
- сервер принимает `v1`, `v2`, `v3` и `v4`;
- клиент валидирует версию ответов;
- в `v4` gap по `SEQUENCE` приводит к `Recover` и повторной отправке неподтверждённого хвоста; в `v1`/`v2`/`v3` такая сессия закрывается;
- компрессия доступна в `v2`, `v3` и `v4`;
- HTTP relay доступен в `v3` и `v4`.

Crypto-handshake:

- магии `TSC2` и `TSS2`;
- pre-shared key + HMAC + timestamp + handshake counter;
- replay-защита через bounded cache и replay window;
- выбор AEAD-алгоритма клиентом с подтверждением сервера;
- HKDF-разделение на MAC-key и transport-key;
- после handshake весь трафик идёт через AEAD duplex stream.

Поддерживаемые AEAD:

- `ChaCha20-Poly1305`
- `AES-256-GCM`
- `AEGIS-128L`
- `AEGIS-256`

### Ключевые архитектурные решения

1. Multiplexing нескольких session в одном TCP-туннеле.
2. Sticky-привязка SOCKS5 TCP-session к выбранному tunnel-серверу.
3. Failover на следующий сервер при отказе `CONNECT`.
4. Прозрачный reconnect и replay неподтверждённого хвоста активных TCP/UDP session.
5. Выделенный protocol/crypto слой отдельно от host-приложений.
6. Split-tunnel как часть SOCKS5/HTTP routing policy, а не как внешний post-processing.
7. Hot path минимизирует GC-аллокации за счёт span-based сериализации, zero-copy UDP payload view, pool-backed хранения pending outbound payload для replay/recovery, leased/pool-backed server-side frame read для транзитных tunnel payload и `64 KiB` relay/body chunking в TCP/HTTP data plane, чтобы уменьшить число protocol frame на крупных transfer.

### Ограничения текущей версии

- нет ротации pre-shared key;
- session migration остаётся `at-least-once`, а не `exactly-once`: после restart tunnel-сервера upstream-side state пересоздаётся, поэтому приложения должны быть готовы к повторной доставке неподтверждённого хвоста;
- HTTP proxy пока поддерживает только `http://`; `CONNECT` и `https://` нужно отправлять через локальный SOCKS5 proxy;
- long-lived HTTP streaming (`SSE`, chunked response) поддерживается, но при reconnect/fault туннеля активный stream обрывается и должен быть поднят заново приложением;
- WebSocket upgrade через HTTP forward/reverse proxy не поддерживается;
- HTTP reverse proxy тоже работает только для `http://` origin на клиенте;
- server-side reverse proxy пока рассчитан на один активный reverse-proxy клиент без явной client identity/multi-tenant маршрутизации;
- Android VPN пока не поддерживает `Private DNS`/DoT на `:853` через текущий pipeline;
- локальная MAUI/Android сборка зависит от корректно настроенного host Android toolchain.
