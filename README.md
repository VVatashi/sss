# SimpleShadowsocks

Простой Shadowsocks-подобный протокол.

## Быстрый старт (сборка, запуск, тесты)

Требования:
- `.NET SDK 9.0+`

Команды запускать из корня репозитория.

### 1) Сборка

```powershell
dotnet build src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj
dotnet build src\SimpleShadowsocks.Client.Core\SimpleShadowsocks.Client.Core.csproj
dotnet build src\SimpleShadowsocks.Server.Core\SimpleShadowsocks.Server.Core.csproj
dotnet build src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj
dotnet build src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj
dotnet build tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

### 1.1) Сборка Release-бинарников (Windows/macOS/Linux)

Ниже команды `framework-dependent` publish (требуется установленный .NET runtime на целевой машине).

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

# macOS ARM64 (Apple Silicon)
dotnet publish src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj -c Release -r osx-arm64 --self-contained false
dotnet publish src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj -c Release -r osx-arm64 --self-contained false
```

Результат publish:
- `bin/SimpleShadowsocks.Server/Release/net9.0/<RID>/publish`
- `bin/SimpleShadowsocks.Client/Release/net9.0/<RID>/publish`

### 1.2) Пример запуска сервера как systemd unit (Linux)

Пример для `linux-x64` publish.

1. Опубликовать сервер:

```bash
dotnet publish src/SimpleShadowsocks.Server/SimpleShadowsocks.Server.csproj -c Release -r linux-x64 --self-contained false
```

2. Скопировать publish-директорию на сервер, например в `/opt/simple-shadowsocks/server`.

3. Создать unit-файл `/etc/systemd/system/simple-shadowsocks-server.service`:

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
ExecStart=/usr/bin/dotnet /opt/simple-shadowsocks/server/SimpleShadowsocks.Server.dll 8388
Restart=always
RestartSec=2
Environment=DOTNET_ENVIRONMENT=Production

# Опционально поднять лимит файловых дескрипторов под нагрузкой
LimitNOFILE=65535

[Install]
WantedBy=multi-user.target
```

4. Применить и запустить:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now simple-shadowsocks-server
sudo systemctl status simple-shadowsocks-server
```

5. Смотреть логи:

```bash
journalctl -u simple-shadowsocks-server -f
```

### 2) Настройка ключа

Клиент и сервер должны использовать одинаковый `SharedKey`.

Файлы конфигурации:
- `src/SimpleShadowsocks.Client/appsettings.json`
- `src/SimpleShadowsocks.Server/appsettings.json`

Поддерживаемые форматы `SharedKey`:
- `hex:<64 hex символа>` (ровно 32 байта)
- `base64:<...>` (должно декодироваться ровно в 32 байта)
- обычная строка-пароль (из нее вычисляется `SHA-256`, 32 байта)

Параметры crypto policy (в `appsettings.json` клиента и сервера):
- `HandshakeMaxClockSkewSeconds` - максимально допустимое расхождение времени при handshake.
- `ReplayWindowSeconds` - окно хранения идентификаторов handshake для защиты от повторов.

Параметры connection policy (в `appsettings.json` клиента):
- `HeartbeatIntervalSeconds` - интервал heartbeat (`Ping`) по туннелю.
- `IdleTimeoutSeconds` - максимальный простой без входящих кадров до принудительного разрыва туннеля.
- `ReconnectBaseDelayMs` - базовая задержка reconnect.
- `ReconnectMaxDelayMs` - верхняя граница задержки reconnect.
- `ReconnectMaxAttempts` - число попыток reconnect перед ошибкой.
- `MaxConcurrentSessions` - лимит активных мультиплексированных сессий на один клиентский туннель.
- `SessionReceiveChannelCapacity` - размер буфера входящих DATA-кадров на сессию (backpressure).

Параметры выбора tunnel-серверов (в `appsettings.json` клиента):
- `RemoteHost`, `RemotePort` - одиночный сервер (обратная совместимость).
- `RemoteServers` - список серверов для балансировки `round-robin`, формат:
  - `{ "Host": "1.2.3.4", "Port": 8388 }`
  - `{ "Host": "tunnel-2.example.org", "Port": 8388 }`

Если `RemoteServers` не пустой, клиент использует именно его.  
Если переданы CLI-аргументы `remoteHost/remotePort`, они переопределяют список и включают режим одиночного сервера.

Параметры протокола (в `appsettings.json` клиента):
- `ProtocolVersion` - версия протокола кадров (`1` или `2`).
- `EnableCompression` - включение сжатия payload в `v2` (`false` по умолчанию).
- `CompressionAlgorithm` - алгоритм сжатия payload в `v2`: `Deflate`, `Gzip`, `Brotli` (`Deflate` по умолчанию).
- `TunnelCipherAlgorithm` - AEAD-алгоритм туннеля: `ChaCha20Poly1305`, `Aes256Gcm`, `Aegis128L`, `Aegis256`.

Параметры server policy (в `appsettings.json` сервера):
- `MaxConcurrentTunnels` - лимит одновременных tunnel-соединений.
- `MaxSessionsPerTunnel` - лимит сессий в одном tunnel-соединении.
- `ConnectTimeoutMs` - таймаут подключения сервера к целевому upstream-хосту для кадра `CONNECT`.

### 3) Запуск

Сначала сервер туннеля:
```powershell
dotnet run --project src\SimpleShadowsocks.Server -- 8388
```

Потом клиентский SOCKS5-прокси:
```powershell
dotnet run --project src\SimpleShadowsocks.Client -- 1080 127.0.0.1 8388
```

После запуска клиент слушает `127.0.0.1:1080`.

Аргументы:
- Client: `<listenPort> <remoteHost> <remotePort> [sharedKey]`
- Server: `<listenPort> [sharedKey]`

### 4) Тесты

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

Категории тестов:
- `Unit` - быстрые проверки кодека и crypto-handshake.
- `Integration` - SOCKS5 и tunnel end-to-end сценарии.
- `Performance` - длительные perf-замеры throughput/allocations.

Физическая структура тестового проекта:
- `tests/SimpleShadowsocks.Client.Tests/Infrastructure` - общие test helpers, категории и сетевые harness-утилиты.
- `tests/SimpleShadowsocks.Client.Tests/Unit/Protocol` - unit-тесты `SimpleShadowsocks.Protocol`.
- `tests/SimpleShadowsocks.Client.Tests/Features/ClientCore/Socks5` - feature/integration тесты SOCKS5-клиента из `SimpleShadowsocks.Client.Core`.
- `tests/SimpleShadowsocks.Client.Tests/Features/ClientCore/Tunnel` - feature/integration тесты tunnel/multiplexing/reconnect для `SimpleShadowsocks.Client.Core`.
- `tests/SimpleShadowsocks.Client.Tests/Performance` - матричные perf-тесты `cipher x compression` и общая perf-harness логика.

Последовательный запуск по стадиям:

```powershell
pwsh .\tests\run-tests.ps1
```

Ручной запуск по категориям:

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Unit"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Integration"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj --filter "Category=Performance"
```

Perf-матрицы `cipher x compression` (Release):

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj -c Release --filter "Category=Performance" --logger "console;verbosity=detailed"
```

Точечный perf/profile запуск можно ограничивать через env vars:

```powershell
$env:SS_PERF_CIPHERS = "Aes256Gcm"
$env:SS_PERF_COMPRESSIONS = "off,Deflate"
$env:SS_PERF_CHUNK_KB = "64"
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj -c Release --filter "FullyQualifiedName~PerformanceMeasurementsTests.Measure_Throughput_And_Allocations_Matrix_MixedNoisePayload" --logger "console;verbosity=detailed"
```

Примечание по perf-тестам:
- добавлено подробное stage-логирование (`[perf] ...`) для диагностики зависаний;
- добавлены встроенные таймауты на этапы `warmup` и `measurement`, чтобы тест не зависал бесконечно.
- `MixedNoise` использует детерминированный набор chunk'ов с псевдослучайным шумом, подготовленный до warmup и measurement, чтобы сжатие не выглядело нереалистично эффективным.
- `Compressible` использует заранее подготовленные хорошо сжимаемые chunk'и для сравнения с шумовым профилем на той же матрице `cipher x compression`.
- timed section для perf-замера начинается только после preconnect всех SOCKS/tunnel-сессий, поэтому handshake/setup больше не занижают throughput.
- порядок `compression`-режимов ротируется между cipher-ами, чтобы `off` не измерялся всегда первым на холодном состоянии.
- доступны env overrides для узкого прогона: `SS_PERF_CIPHERS`, `SS_PERF_COMPRESSIONS`, `SS_PERF_TOTAL_MB`, `SS_PERF_CHUNK_KB`, `SS_PERF_STREAMS`, `SS_PERF_PAYLOAD_VARIANTS`.

## Что уже реализовано

- Решение и проекты на `.NET 9`:
  - `SimpleShadowsocks.Client.Core`
  - `SimpleShadowsocks.Client`
  - `SimpleShadowsocks.Server.Core`
  - `SimpleShadowsocks.Server`
  - `SimpleShadowsocks.Protocol`

- Клиентский SOCKS5:
  - handshake `VER=5`, метод `NO AUTH (0x00)`
  - `CONNECT (0x01)`
  - адреса `IPv4`, `IPv6`, `Domain`
  - корректные SOCKS5 reply-коды

- Внутренний протокол клиент<->сервер:
  - framing/serialization (`ProtocolFrameCodec`, `ProtocolPayloadSerializer`)
  - кадры `Connect/Data/Close/Ping/Pong`
  - передача трафика через туннель `Client <-> Server`
  - мультиплексирование нескольких `sessionId` в одном TCP-туннеле
  - ordering/replay policy на уровне прикладных кадров через монотонный `Sequence` per session
  - heartbeat/idle timeout: клиент отправляет `Ping`, контролирует отсутствие входящего трафика и сбрасывает зависшее туннельное соединение
  - reconnect policy: повторные подключения с экспоненциальной задержкой в пределах настроек policy
  - graceful session migration/resume: при reconnect клиент пытается прозрачно переоткрыть активные `sessionId` (re-`CONNECT`) и продолжить работу существующих SOCKS5 TCP-соединений
  - клиент поддерживает группу tunnel-серверов с выбором `round-robin`
  - привязка TCP-сессии SOCKS5 к выбранному tunnel-серверу (sticky per client TCP session)
  - версия протокола кадров увеличена до `v2` (добавлены flags и опциональное сжатие payload)
  - поддержаны алгоритмы сжатия payload в `v2`: `Deflate`, `Gzip`, `Brotli`
  - алгоритм сжатия кодируется в `FLAGS` каждого кадра `v2` и выбирается клиентом через конфиг
  - сервер совместим с `v1` и `v2`; версия соединения фиксируется по первому кадру клиента
  - при несовместимости версии клиент закрывает соединение с понятной ошибкой
  - снижены лишние аллокации в горячем пути кодека/сериализации:
    - чтение сжатых payload через `ArrayPool<byte>` без промежуточного heap-массива для compressed frame
    - сериализация `CONNECT` для IP/Domain без промежуточных буферов (`TryWriteBytes`/span-based ASCII encode)

- Поточное шифрование туннеля:
  - AEAD-алгоритмы: `ChaCha20-Poly1305`, `AES-256-GCM`, `AEGIS-128L`, `AEGIS-256`
  - клиент выбирает алгоритм через `TunnelCipherAlgorithm` в конфиге
  - сервер поддерживает все перечисленные AEAD-алгоритмы и принимает выбор клиента из crypto-handshake
  - реализации AEAD вынесены в отдельные классы с общим интерфейсом (`IAeadCipherImpl`) и фабрикой выбора алгоритма (`AeadCipherFactory`) без изменения wire-формата
  - для `ChaCha20-Poly1305`/`AES-256-GCM` используются `System.Security.Cryptography` при доступности, fallback - BouncyCastle
  - для `AEGIS-128L`/`AEGIS-256` используется `NSec.Cryptography` (libsodium backend) с runtime-проверкой поддержки
  - pre-shared key из конфигурации
  - защищенный crypto-handshake v2 (HMAC + timestamp + handshake counter + negotiated algorithm) c проверкой времени на обеих сторонах
  - HKDF-разделение ключевого материала: отдельный ключ для MAC handshake и отдельный transport key для AEAD
  - последующий обмен всеми кадрами в зашифрованном и аутентифицированном виде
  - nonce policy: уникальный nonce на каждый AEAD-record через base nonce + counter
  - защита от повторного использования nonce при исчерпании счетчика (требуется re-key)
  - replay protection на сервере для handshake (bounded cache + replay window)
  - снижены лишние аллокации в AEAD data path: nonce и 4-byte length prefix больше не создаются заново на каждый record

- Сервер туннеля:
  - принимает `CONNECT` кадр
  - подключается к целевому хосту с ограничением по `ConnectTimeoutMs`
  - возвращает код результата
  - передает `DATA` в обе стороны
  - держит несколько независимых upstream-сессий в одном соединении с клиентом
  - ограничивает число tunnel-соединений и число сессий на туннель (hard limits)
  - обработка `CONNECT` выполняется неблокирующе для read-loop туннеля (медленный/недоступный upstream не блокирует другие сессии)
  - убран `FlushAsync` на каждый `DATA` frame в `upstream`, чтобы не форсировать лишние syscalls на горячем пути

- Тесты:
  - unit + integration тесты SOCKS5, протокола и туннеля
  - есть узкий crypto regression-тест handshake+record roundtrip для `AEGIS-128L`
  - есть проверка multiplexing: две сессии через один туннель
  - есть проверка reconnect после перезапуска tunnel-сервера
  - есть проверка graceful migration активной сессии через reconnect туннеля
  - есть проверка ограничения сессий на сервере и валидации reconnect policy
  - есть проверка round-robin распределения по группе tunnel-серверов
  - есть проверка, что медленный/таймаутный `CONNECT` не блокирует другие сессии
  - есть проверка туннеля с `AES-256-GCM`
  - есть проверки туннеля с `AEGIS-128L` и `AEGIS-256`
  - есть две perf-матрицы `cipher x compression`: для `MixedNoise` и для `Compressible`
  - есть проверки `v1/v2` и round-trip со сжатием в `v2`
  - профиль `MixedNoise` предвычисляется как псевдослучайный шум до начала измерения
  - текущий набор: `32` теста, проходят

- Артефакты сборки вынесены в корневые каталоги:
  - `bin/<ProjectName>/...`
  - `obj/<ProjectName>/...`

## Текущая структура проекта

- `src/SimpleShadowsocks.Client.Core` - runtime-код локального SOCKS5-proxy и клиента туннеля.
  - `Socks5/Socks5Server.cs` - lifecycle, accept-loop, выбор tunnel-сервера.
  - `Socks5/Socks5Server.Protocol.cs` - SOCKS5 handshake, парсинг `CONNECT`, reply.
  - `Socks5/Socks5Server.Relay.cs` - direct relay и relay через multiplexed tunnel.
  - `Tunnel/TunnelClientMultiplexer.cs` - public API и базовое состояние multiplexer.
  - `Tunnel/TunnelClientMultiplexer.Connection.cs` - connect/reconnect lifecycle и cleanup соединения.
  - `Tunnel/TunnelClientMultiplexer.Loops.cs` - read/write/heartbeat loops.
  - `Tunnel/TunnelClientMultiplexer.Sessions.cs` - open/restore/send логика tunnel-сессий.
  - `Tunnel/TunnelClientMultiplexer.Models.cs` - внутренние модели состояния сессии и outbound frame.
- `src/SimpleShadowsocks.Client` - thin launcher клиента, конфиг и `Program.cs`.
- `src/SimpleShadowsocks.Server.Core` - runtime-код сервера туннеля.
  - `Tunnel/TunnelServer.cs` - server policy, accept-loop и lifecycle tunnel-соединений.
  - `Tunnel/TunnelServer.Connection.cs` - handshake tunnel-соединения и dispatch входящих кадров.
  - `Tunnel/TunnelServer.Connect.cs` - обработка `CONNECT` и подключение к upstream.
  - `Tunnel/TunnelServer.Sessions.cs` - lifecycle tunnel-сессий, отправка кадров и `SessionContext`.
- `src/SimpleShadowsocks.Server` - thin launcher сервера, конфиг и `Program.cs`.
- `src/SimpleShadowsocks.Protocol` - модели протокола, кодек и crypto-утилиты.
- `tests/SimpleShadowsocks.Client.Tests` - тестовый проект с вертикальным делением по типу теста и горизонтальным по подсистемам.
  - `Infrastructure` - общие test helpers и categories.
  - `Unit/Protocol` - unit-покрытие кодека и crypto-handshake для `SimpleShadowsocks.Protocol`.
  - `Features/ClientCore/Socks5` - feature/integration сценарии SOCKS5.
  - `Features/ClientCore/Tunnel` - feature/integration сценарии tunnel/reconnect/multiplexing.
  - `Performance` - perf-матрицы и общие perf helpers.
  - тестовый проект подключает `SimpleShadowsocks.Client.Core`, `SimpleShadowsocks.Server.Core` и `SimpleShadowsocks.Protocol` через `ProjectReference`.

## Формат и правила протокола

Поддерживаются две версии framing:

`v1` (legacy, без flags):
```text
+--------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-------------+-----------+--------------+
```

`v2` (текущая, с flags):
```text
+--------+----------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | FLAGS(1) | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+----------+------------+-------------+-----------+--------------+
```

Где:
- `VER`: версия кадра (`1` или `2`).
- `TYPE`: `Connect(1)`, `Data(2)`, `Close(3)`, `Ping(4)`, `Pong(5)`.
- `FLAGS` (только `v2`):
  - `0x01` (`PayloadCompressed`) - `PAYLOAD` сжат.
  - `0x02` (`CompressionEnabled`) - сторона поддерживает/включила сжатие на этом соединении.
  - `0x0C` (`CompressionAlgorithmMask`) - код алгоритма сжатия:
    - `00` = `Deflate`
    - `01` = `Gzip`
    - `10` = `Brotli`
- `SESSION`: ID логической мультиплексированной сессии (`0` зарезервирован для control-frame, например heartbeat).
- `SEQUENCE`: монотонный счётчик кадров внутри `SESSION`.
- `LEN`: длина `PAYLOAD` в байтах.
- `PAYLOAD`: полезные данные кадра.

Правила обработки:
- Максимальный размер payload: `1 MiB` (`ProtocolConstants.MaxPayloadLength`).
- Сервер принимает `v1` и `v2`; версия соединения фиксируется по первому кадру и не может меняться в рамках одного tunnel-соединения.
- Клиент проверяет, что ответы приходят в ожидаемой версии, иначе закрывает соединение с понятной ошибкой.
- Для `v2` алгоритм сжатия выбирается клиентом (`CompressionAlgorithm`) и кодируется в `FLAGS` кадра.
- Ordering/replay policy на прикладных кадрах: ожидается строго возрастающий `SEQUENCE` per `SESSION`; при нарушении сессия закрывается.

Crypto-handshake (transport encryption):
- Используется handshake версии `v2` (магии `TSC2`/`TSS2`).
- Клиент передаёт выбранный AEAD-алгоритм в `ClientHello`.
- Сервер отвечает `ServerHello` с подтверждённым алгоритмом (в текущей реализации равным выбору клиента).
- При несовпадении/неподдерживаемом алгоритме handshake завершается ошибкой.

## Ограничения текущей версии

- Нет ротации ключей.
- При нарушении sequence policy сессия закрывается без механизма selective recovery/retransmit.
- Session migration/resume выполняется best-effort: при длительном недоступном upstream или ошибке повторного `CONNECT` конкретная сессия завершается.

## Результат матрицы производительности (Release)

Тесты:
- `PerformanceMeasurementsTests.Measure_Throughput_And_Allocations_Matrix_MixedNoisePayload`
- `PerformanceMeasurementsTests.Measure_Throughput_And_Allocations_Matrix_CompressiblePayload`

Параметры: `128 MiB`, `64 KiB`, `4 streams`.

| Payload | Cipher | Compression | Throughput (MiB/s) | Alloc/MiB (bytes) | Tunnel C->S (bytes) | Tunnel S->C (bytes) | Tunnel Total (bytes) |
|---|---|---|---:|---:|---:|---:|---:|
| `MixedNoise` | `ChaCha20Poly1305` | `off` | 125.76 | 2 283 688 | 134 701 115 | 134 701 115 | 269 402 230 |
| `MixedNoise` | `ChaCha20Poly1305` | `Deflate` | 80.56 | 2 431 463 | 134 701 056 | 134 701 056 | 269 402 112 |
| `MixedNoise` | `ChaCha20Poly1305` | `Gzip` | 80.68 | 2 397 237 | 134 701 056 | 134 701 056 | 269 402 112 |
| `MixedNoise` | `ChaCha20Poly1305` | `Brotli` | 107.72 | 2 374 116 | 134 701 056 | 134 701 056 | 269 402 112 |
| `MixedNoise` | `Aes256Gcm` | `off` | 233.40 | 2 246 878 | 134 701 115 | 134 701 115 | 269 402 230 |
| `MixedNoise` | `Aes256Gcm` | `Deflate` | 108.50 | 2 379 194 | 134 701 056 | 134 701 056 | 269 402 112 |
| `MixedNoise` | `Aes256Gcm` | `Gzip` | 115.03 | 2 380 323 | 134 701 056 | 134 701 056 | 269 402 112 |
| `MixedNoise` | `Aes256Gcm` | `Brotli` | 124.48 | 2 361 796 | 134 701 056 | 134 701 056 | 269 402 112 |
| `MixedNoise` | `Aegis128L` | `off` | 307.71 | 2 209 200 | 134 963 200 | 134 963 200 | 269 926 400 |
| `MixedNoise` | `Aegis128L` | `Deflate` | 111.68 | 2 344 754 | 134 963 200 | 134 963 200 | 269 926 400 |
| `MixedNoise` | `Aegis128L` | `Gzip` | 116.16 | 2 344 999 | 134 963 200 | 134 963 200 | 269 926 400 |
| `MixedNoise` | `Aegis128L` | `Brotli` | 131.80 | 2 321 708 | 134 963 200 | 134 963 200 | 269 926 400 |
| `MixedNoise` | `Aegis256` | `off` | 265.81 | 2 208 204 | 134 963 401 | 134 963 473 | 269 926 874 |
| `MixedNoise` | `Aegis256` | `Deflate` | 101.85 | 2 349 263 | 134 963 200 | 134 963 200 | 269 926 400 |
| `MixedNoise` | `Aegis256` | `Gzip` | 112.55 | 2 352 110 | 134 963 200 | 134 963 200 | 269 926 400 |
| `MixedNoise` | `Aegis256` | `Brotli` | 130.85 | 2 322 812 | 134 963 200 | 134 963 200 | 269 926 400 |
| `Compressible` | `ChaCha20Poly1305` | `off` | 160.65 | 2 268 136 | 134 701 095 | 134 701 115 | 269 402 210 |
| `Compressible` | `ChaCha20Poly1305` | `Deflate` | 45.27 | 2 314 580 | 1 675 196 | 1 908 736 | 3 583 932 |
| `Compressible` | `ChaCha20Poly1305` | `Gzip` | 50.09 | 2 316 012 | 1 824 332 | 2 056 192 | 3 880 524 |
| `Compressible` | `ChaCha20Poly1305` | `Brotli` | 45.77 | 2 264 238 | 481 812 | 704 512 | 1 186 324 |
| `Compressible` | `Aes256Gcm` | `off` | 268.00 | 2 246 197 | 134 701 056 | 134 701 056 | 269 402 112 |
| `Compressible` | `Aes256Gcm` | `Deflate` | 46.10 | 2 316 333 | 1 679 356 | 1 908 736 | 3 588 092 |
| `Compressible` | `Aes256Gcm` | `Gzip` | 43.35 | 2 324 447 | 1 820 975 | 2 056 275 | 3 877 250 |
| `Compressible` | `Aes256Gcm` | `Brotli` | 46.25 | 2 272 538 | 482 452 | 704 512 | 1 186 964 |
| `Compressible` | `Aegis128L` | `off` | 356.63 | 2 206 362 | 134 963 492 | 134 963 382 | 269 926 874 |
| `Compressible` | `Aegis128L` | `Deflate` | 48.28 | 2 290 891 | 1 751 732 | 2 170 880 | 3 922 612 |
| `Compressible` | `Aegis128L` | `Gzip` | 46.09 | 2 299 479 | 1 895 948 | 2 318 336 | 4 214 284 |
| `Compressible` | `Aegis128L` | `Brotli` | 40.73 | 2 247 869 | 560 252 | 966 656 | 1 526 908 |
| `Compressible` | `Aegis256` | `off` | 287.21 | 2 207 305 | 134 963 274 | 134 963 291 | 269 926 565 |
| `Compressible` | `Aegis256` | `Deflate` | 44.51 | 2 290 230 | 1 755 358 | 2 170 880 | 3 926 238 |
| `Compressible` | `Aegis256` | `Gzip` | 48.43 | 2 298 841 | 1 897 676 | 2 318 336 | 4 216 012 |
| `Compressible` | `Aegis256` | `Brotli` | 49.31 | 2 256 725 | 562 628 | 966 656 | 1 529 284 |

Вывод:
- на профиле `MixedNoise` сжатие практически не уменьшает tunnel traffic, что и ожидается для данных, похожих на случайный шум; при дефолтном `64 KiB` chunk `compression=off` ожидаемо быстрее compression-вариантов;
- на профиле `Compressible` `Deflate`/`Gzip`/`Brotli` резко уменьшают объём tunnel traffic, причём `Brotli` даёт минимальный трафик в этой матрице;
- throughput здесь зависит от конкретной реализации алгоритма и текущего хоста, а не только от степени сжатия;
- смена дефолтного chunk size с `16 KiB` на `64 KiB` убрала вводящий в заблуждение latency-bound сценарий: `off` больше не выглядит аномально медленным на шумовых данных.

## Следующие шаги

1. Добавить ротацию pre-shared key и процедуру key update.
2. Добавить метрики по sequence violations и отказам сессий.
3. Добавить persist replay-cache (или distributed replay-cache) для multi-instance server deployment.
4. Добавить deadline/timeout policy для операции session-resume (отдельно от reconnect policy).
