# SimpleShadowsocks

Простой Shadowsocks-подобный протокол.

## Быстрый старт (сборка, запуск, тесты)

Требования:
- `.NET SDK 9.0+`

Команды запускать из корня репозитория.

### 1) Сборка

```powershell
dotnet build src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj
dotnet build src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj
dotnet build src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj
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
- `EnableCompression` - включение сжатия payload в `v2` (`false` по умолчанию), алгоритм фиксирован: `Deflate` (`CompressionLevel.Fastest`).

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

Отдельно perf-замер (Release):

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj -c Release --filter "FullyQualifiedName~PerformanceMeasurementsTests" --logger "console;verbosity=detailed"
```

## Что уже реализовано

- Решение и проекты на `.NET 9`:
- `SimpleShadowsocks.Client`
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
- клиент поддерживает группу tunnel-серверов с выбором `round-robin`
- привязка TCP-сессии SOCKS5 к выбранному tunnel-серверу (sticky per client TCP session)
- версия протокола кадров увеличена до `v2` (добавлены flags и опциональное сжатие payload)
- алгоритм сжатия payload в `v2` зафиксирован на `Deflate` (без выбора/negotiation алгоритма)
- сервер совместим с `v1` и `v2`; версия соединения фиксируется по первому кадру клиента
- при несовместимости версии клиент закрывает соединение с понятной ошибкой
- снижены лишние аллокации в горячем пути кодека/сериализации:
- чтение сжатых payload через `ArrayPool<byte>` без промежуточного heap-массива для compressed frame
- сериализация `CONNECT` для IP/Domain без промежуточных буферов (`TryWriteBytes`/span-based ASCII encode)

- Поточное шифрование туннеля:
- алгоритм `ChaCha20-Poly1305 (AEAD)`: приоритет `System.Security.Cryptography.ChaCha20Poly1305`, fallback на BouncyCastle при недоступности платформенной реализации
- pre-shared key из конфигурации
- защищенный handshake (HMAC + timestamp + handshake counter) c проверкой времени на обеих сторонах
- HKDF-разделение ключевого материала: отдельный ключ для MAC handshake и отдельный transport key для AEAD
- последующий обмен всеми кадрами в зашифрованном и аутентифицированном виде
- nonce policy: уникальный nonce на каждый AEAD-record через base nonce + counter
- защита от повторного использования nonce при исчерпании счетчика (требуется re-key)
- replay protection на сервере для handshake (bounded cache + replay window)

- Сервер туннеля:
- принимает `CONNECT` кадр
- подключается к целевому хосту с ограничением по `ConnectTimeoutMs`
- возвращает код результата
- передает `DATA` в обе стороны
- держит несколько независимых upstream-сессий в одном соединении с клиентом
- ограничивает число tunnel-соединений и число сессий на туннель (hard limits)
- обработка `CONNECT` выполняется неблокирующе для read-loop туннеля (медленный/недоступный upstream не блокирует другие сессии)

- Тесты:
- unit + integration тесты SOCKS5, протокола и туннеля
- есть проверка multiplexing: две сессии через один туннель
- есть проверка reconnect после перезапуска tunnel-сервера
- есть проверка ограничения сессий на сервере и валидации reconnect policy
- есть проверка round-robin распределения по группе tunnel-серверов
- есть проверка, что медленный/таймаутный `CONNECT` не блокирует другие сессии
- есть perf-тест для измерения throughput/allocations
- есть проверки `v1/v2` и round-trip со сжатием в `v2`
- есть отдельный perf-тест на хорошо сжимаемом payload
- текущий набор: `24` тестов, проходят

- Артефакты сборки вынесены в корневые каталоги:
- `bin/<ProjectName>/...`
- `obj/<ProjectName>/...`

## Текущая структура проекта

- `src/SimpleShadowsocks.Client` - локальный SOCKS5-proxy, клиент туннеля.
- `src/SimpleShadowsocks.Server` - сервер туннеля.
- `src/SimpleShadowsocks.Protocol` - модели протокола, кодек и crypto-утилиты.
- `tests/SimpleShadowsocks.Client.Tests` - unit/integration тесты.

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
- `0x01` (`PayloadCompressed`) - `PAYLOAD` сжат `Deflate`.
- `0x02` (`CompressionEnabled`) - сторона поддерживает/включила сжатие на этом соединении.
- `SESSION`: ID логической мультиплексированной сессии (`0` зарезервирован для control-frame, например heartbeat).
- `SEQUENCE`: монотонный счётчик кадров внутри `SESSION`.
- `LEN`: длина `PAYLOAD` в байтах.
- `PAYLOAD`: полезные данные кадра.

Правила обработки:
- Максимальный размер payload: `1 MiB` (`ProtocolConstants.MaxPayloadLength`).
- Сервер принимает `v1` и `v2`; версия соединения фиксируется по первому кадру и не может меняться в рамках одного tunnel-соединения.
- Клиент проверяет, что ответы приходят в ожидаемой версии, иначе закрывает соединение с понятной ошибкой.
- Для `v2` сжатие фиксировано на `Deflate (CompressionLevel.Fastest)`, выбор алгоритма не negotiated.
- Ordering/replay policy на прикладных кадрах: ожидается строго возрастающий `SEQUENCE` per `SESSION`; при нарушении сессия закрывается.

## Ограничения текущей версии

- Нет ротации ключей.
- При нарушении sequence policy сессия закрывается без механизма selective recovery/retransmit.
- Reconnect не восстанавливает уже открытые SOCKS5-сессии: активные сессии закрываются и клиентские приложения должны открыть новое соединение.

## Результат измерений сжатия (Release)

Тест: `PerformanceMeasurementsTests`, `128 MiB`, `16 KiB`, `4 streams`.

Профиль `mixed` (слабо сжимаемый):
- Без сжатия: `43.72 MiB/s`, `2,337,619 bytes/MiB`.
- Со сжатием: `37.12 MiB/s`, `2,409,587 bytes/MiB`.

Профиль `compressible` (повторяющиеся последовательности байтов):
- Без сжатия: `44.77 MiB/s`, `2,325,944 bytes/MiB`.
- Со сжатием: `34.88 MiB/s`, `2,379,907 bytes/MiB`.

Вывод: в текущей реализации (`Deflate`) сжатие проигрывает по throughput в обоих профилях и не даёт выигрыша по аллокациям.  
По умолчанию оставлено `EnableCompression=false`.

## Следующие шаги

1. Добавить ротацию pre-shared key и процедуру key update.
2. Добавить метрики по sequence violations и отказам сессий.
3. Добавить persist replay-cache (или distributed replay-cache) для multi-instance server deployment.
4. Добавить graceful session migration/resume при reconnect (сейчас сессии закрываются).
