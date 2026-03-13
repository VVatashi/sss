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

- Поточное шифрование туннеля:
- алгоритм `ChaCha20-Poly1305 (AEAD)` (BouncyCastle)
- pre-shared key из конфигурации
- защищенный nonce-handshake (HMAC + timestamp + handshake counter)
- последующий обмен всеми кадрами в зашифрованном и аутентифицированном виде
- nonce policy: уникальный nonce на каждый AEAD-record через base nonce + counter
- защита от повторного использования nonce при исчерпании счетчика (требуется re-key)
- replay protection на сервере для handshake (cache + replay window)

- Сервер туннеля:
- принимает `CONNECT` кадр
- подключается к целевому хосту
- возвращает код результата
- передает `DATA` в обе стороны

- Тесты:
- unit + integration тесты SOCKS5, протокола и туннеля
- текущий набор: `13` тестов, проходят

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
+--------+----------+------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-----------+--------------+
```

Поля:
- `VER` - версия протокола.
- `TYPE` - `Connect`, `Data`, `Close`, `Ping`, `Pong`.
- `SESSION` - идентификатор логической сессии.
- `LEN` - длина payload.
- `PAYLOAD` - полезные данные.

## Ограничения текущей версии

- В текущем сервере обрабатывается одна логическая сессия на одно туннельное TCP-подключение.
- Нет ротации ключей.
- Replay protection реализован для этапа handshake, но не для каждого прикладного кадра протокола поверх активной сессии.

## Следующие шаги

1. Добавить мультиплексирование нескольких `sessionId` в одном туннеле.
2. Добавить replay/ordering policy на уровне прикладных кадров (не только handshake).
3. Добавить heartbeat/idle timeout и reconnect policy.
4. Добавить ротацию pre-shared key и процедуру key update.
