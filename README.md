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

### 2) Запуск

Сначала сервер туннеля:
```powershell
dotnet run --project src\SimpleShadowsocks.Server -- 8388
```

Потом клиентский SOCKS5-прокси (порт SOCKS5 + host/port сервера туннеля):
```powershell
dotnet run --project src\SimpleShadowsocks.Client -- 1080 127.0.0.1 8388
```

После запуска клиент слушает `127.0.0.1:1080`.

### 3) Тесты

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

## Что уже реализовано

- Решение и проекты на `.NET 9`:
- `SimpleShadowsocks.Client`
- `SimpleShadowsocks.Server`
- `SimpleShadowsocks.Protocol`

- Клиентский SOCKS5-сервер:
- handshake `VER=5`, метод `NO AUTH (0x00)`
- `CONNECT (0x01)`
- адреса `IPv4`, `IPv6`, `Domain`
- корректные SOCKS5 reply-коды

- Внутренний протокол клиент<->сервер:
- framing/serialization (`ProtocolFrameCodec`, `ProtocolPayloadSerializer`)
- кадры `Connect/Data/Close/Ping/Pong`
- `CONNECT` запрос отправляется клиентом на сервер туннеля
- двунаправленная ретрансляция `DATA` через туннель
- корректное завершение через `CLOSE`

- Сервер туннеля:
- принимает `CONNECT` кадр
- подключается к целевому хосту
- возвращает код результата
- передает трафик в обе стороны

- Тесты:
- unit + integration тесты протокола/SOCKS5/туннеля
- текущий набор: `13` тестов, проходят

- Артефакты сборки вынесены в корневые каталоги:
- `bin/<ProjectName>/...`
- `obj/<ProjectName>/...`

## Текущая структура проекта

- `src/SimpleShadowsocks.Client` - локальный SOCKS5-proxy, клиент туннеля.
- `src/SimpleShadowsocks.Server` - сервер туннеля.
- `src/SimpleShadowsocks.Protocol` - модели и кодек бинарного протокола.
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

- Пока нет шифрования и аутентификации туннеля.
- В текущем сервере обрабатывается одна логическая сессия на одно туннельное TCP-подключение.
- Нет конфигурации через `appsettings.json` (параметры через аргументы командной строки).

## Следующие шаги

1. Добавить шифрование канала (MVP: pre-shared key + AEAD).
2. Добавить мультиплексирование нескольких `sessionId` в одном туннеле.
3. Добавить heartbeat/idle timeout и политику reconnect.
4. Добавить конфигурацию через `appsettings.json` и structured logging.
