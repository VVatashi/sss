# SimpleShadowsocks

## Быстрый старт (сборка, запуск, тесты)

Требования:
- `.NET SDK 9.0+`

Команды запускать из корня репозитория.

### 1) Сборка

```powershell
dotnet build src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj
dotnet build src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj
dotnet build src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj
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

Параметры server policy (в `appsettings.json` сервера):
- `MaxConcurrentTunnels` - лимит одновременных tunnel-соединений.
- `MaxSessionsPerTunnel` - лимит сессий в одном tunnel-соединении.

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

- Поточное шифрование туннеля:
- алгоритм `ChaCha20-Poly1305 (AEAD)` (BouncyCastle)
- pre-shared key из конфигурации
- защищенный handshake (HMAC + timestamp + handshake counter) c проверкой времени на обеих сторонах
- HKDF-разделение ключевого материала: отдельный ключ для MAC handshake и отдельный transport key для AEAD
- последующий обмен всеми кадрами в зашифрованном и аутентифицированном виде
- nonce policy: уникальный nonce на каждый AEAD-record через base nonce + counter
- защита от повторного использования nonce при исчерпании счетчика (требуется re-key)
- replay protection на сервере для handshake (bounded cache + replay window)

- Сервер туннеля:
- принимает `CONNECT` кадр
- подключается к целевому хосту
- возвращает код результата
- передает `DATA` в обе стороны
- держит несколько независимых upstream-сессий в одном соединении с клиентом
- ограничивает число tunnel-соединений и число сессий на туннель (hard limits)

- Тесты:
- unit + integration тесты SOCKS5, протокола и туннеля
- есть проверка multiplexing: две сессии через один туннель
- есть проверка reconnect после перезапуска tunnel-сервера
- есть проверка ограничения сессий на сервере и валидации reconnect policy
- текущий набор: `18` тестов, проходят

- Артефакты сборки вынесены в корневые каталоги:
- `bin/<ProjectName>/...`
- `obj/<ProjectName>/...`

## Текущая структура проекта

- `src/SimpleShadowsocks.Client` - локальный SOCKS5-proxy, клиент туннеля.
- `src/SimpleShadowsocks.Server` - сервер туннеля.
- `src/SimpleShadowsocks.Protocol` - модели протокола, кодек и crypto-утилиты.
- `tests/SimpleShadowsocks.Client.Tests` - unit/integration тесты.

## Формат кадра протокола

```text
+--------+----------+------------+-------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | SEQUENCE(8) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-------------+-----------+--------------+
```

Поля:
- `VER` - версия протокола.
- `TYPE` - `Connect`, `Data`, `Close`, `Ping`, `Pong`.
- `SESSION` - идентификатор логической сессии.
- `SEQUENCE` - монотонный счетчик кадров в рамках `SESSION`.
- `LEN` - длина payload.
- `PAYLOAD` - полезные данные.

## Ограничения текущей версии

- Нет ротации ключей.
- При нарушении sequence policy сессия закрывается без механизма selective recovery/retransmit.
- Reconnect не восстанавливает уже открытые SOCKS5-сессии: активные сессии закрываются и клиентские приложения должны открыть новое соединение.

## Следующие шаги

1. Добавить ротацию pre-shared key и процедуру key update.
2. Добавить метрики по sequence violations и отказам сессий.
3. Добавить persist replay-cache (или distributed replay-cache) для multi-instance server deployment.
4. Добавить graceful session migration/resume при reconnect (сейчас сессии закрываются).
