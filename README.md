# SimpleShadowsocks

SimpleShadowsocks - это компактный Shadowsocks-подобный стек на .NET 9 для личного туннеля и тестовых окружений. В репозитории есть:

- `SimpleShadowsocks.Server` - CLI tunnel endpoint для VPS/сервера;
- `SimpleShadowsocks.Client` - локальный CLI SOCKS5-клиент с routing rules, failover и Windows full-tunnel helper;
- `SimpleShadowsocks.Client.Maui` - Android UI/VPN-клиент на .NET MAUI.

Проект поддерживает:

- туннелирование TCP и UDP;
- локальный SOCKS5 `CONNECT` и `UDP ASSOCIATE`;
- несколько upstream tunnel-серверов с `round-robin`/failover;
- split-tunnel через `TrafficRoutingRules`;
- локальную SOCKS5-аутентификацию `USERNAME/PASSWORD`;
- шифрование `ChaCha20-Poly1305`, `AES-256-GCM`, `AEGIS-128L`, `AEGIS-256`;
- сжатие `Deflate`, `Gzip`, `Brotli` для протокола `v2`;
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
2. на клиенте поднимается локальный SOCKS5-прокси;
3. приложения отправляют трафик в этот SOCKS5-прокси;
4. трафик уходит к вашему серверу в зашифрованном виде.

Проект подходит, если нужен:

- простой self-hosted туннель без сложной панели управления;
- локальный SOCKS5 для браузера, системных клиентов и тестовых приложений;
- split-tunnel по доменам и подсетям;
- Android VPN-клиент или Windows full-tunnel поверх локального SOCKS5.

### Состав приложений

#### 1. `SimpleShadowsocks.Server`

CLI-сервер, который:

- принимает туннельные подключения клиентов;
- поддерживает протоколы `v1` и `v2`;
- проксирует TCP и UDP в upstream;
- ограничивает количество туннелей и сессий политиками сервера.

#### 2. `SimpleShadowsocks.Client`

CLI-клиент, который:

- поднимает локальный SOCKS5-сервер;
- умеет работать как с одним `RemoteHost`, так и с набором `RemoteServers`;
- применяет `TrafficRoutingRules` для `CONNECT` и `UDP ASSOCIATE`;
- поддерживает локальную SOCKS5-аутентификацию;
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
  "ConnectTimeoutMs": 10000
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
  "ProtocolVersion": 2,
  "EnableCompression": false,
  "CompressionAlgorithm": "Deflate",
  "TunnelCipherAlgorithm": "ChaCha20Poly1305",
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
- `RemoteServers` используется вместо `RemoteHost`/`RemotePort`, если список не пустой;
- `TrafficRoutingRules` применяются сверху вниз, первое совпавшее правило побеждает;
- если `TrafficRoutingRules` отсутствуют, клиент автоматически использует `* -> Tunnel`;
- `Socks5Authentication.Enabled=true` включает `USERNAME/PASSWORD` для входящих клиентов SOCKS5;
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
3. Укажите в приложении или браузере SOCKS5-прокси `127.0.0.1:1080`.
4. Проверьте, что трафик действительно маршрутизируется через ожидаемое правило `TrafficRoutingRules`.

## Для разработчика

### Технические требования

- `.NET SDK 9.0.312` - версия зафиксирована в `global.json`;
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

Что покрыто тестами:

- SOCKS5 `CONNECT` в standalone и tunnel-режимах;
- `UDP ASSOCIATE`, relay и routing direct/tunnel по destination;
- аутентификация `USERNAME/PASSWORD` для локального SOCKS5;
- failover на следующий tunnel-сервер при отказе `CONNECT`;
- reconnect и миграция активных session после разрыва туннеля;
- разные AEAD-алгоритмы и компрессия `v2`;
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
   - SOCKS5 runtime, routing rules, tunnel client, reconnect/session migration.
3. `SimpleShadowsocks.Server.Core`
   - tunnel server, handshake, session lifecycle, upstream proxying.
4. Хосты:
   - `SimpleShadowsocks.Client`
   - `SimpleShadowsocks.Server`
   - `SimpleShadowsocks.Client.Maui`

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

Поддерживаются две версии framing.

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

Поля:

- `VER` - версия кадра (`1` или `2`);
- `TYPE` - `Connect(1)`, `Data(2)`, `Close(3)`, `Ping(4)`, `Pong(5)`, `UdpAssociate(6)`, `UdpData(7)`;
- `FLAGS` в `v2`:
  - `0x01` - `PayloadCompressed`
  - `0x02` - `CompressionEnabled`
  - `0x0C` - `CompressionAlgorithmMask`
- `SESSION` - идентификатор логической сессии;
- `SEQUENCE` - монотонный счётчик кадров;
- `LEN` - длина payload.

Правила:

- максимальный payload - `1 MiB`;
- сервер принимает `v1` и `v2`;
- клиент валидирует версию ответов;
- при нарушении monotonic `SEQUENCE` сессия закрывается;
- компрессия доступна только в `v2`.

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
4. Прозрачный reconnect и best-effort migration активных session.
5. Выделенный protocol/crypto слой отдельно от host-приложений.
6. Split-tunnel как часть SOCKS5 routing policy, а не как внешний post-processing.

### Ограничения текущей версии

- нет ротации pre-shared key;
- нет selective retransmit/recovery при sequence-ошибках;
- session migration остаётся best-effort;
- Android VPN пока не поддерживает `Private DNS`/DoT на `:853` через текущий pipeline;
- локальная MAUI/Android сборка зависит от корректно настроенного host Android toolchain.
