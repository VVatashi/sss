# SimpleShadowsocks

## Быстрый старт (сборка, запуск, тесты)

Требования:
- `.NET SDK 9.0+`

Команды выполнять из корня репозитория.

### 1) Сборка

```powershell
dotnet build src\SimpleShadowsocks.Protocol\SimpleShadowsocks.Protocol.csproj
dotnet build src\SimpleShadowsocks.Server\SimpleShadowsocks.Server.csproj
dotnet build src\SimpleShadowsocks.Client\SimpleShadowsocks.Client.csproj
```

### 2) Запуск

Сервер (пока каркас):
```powershell
dotnet run --project src\SimpleShadowsocks.Server -- 8388
```

Клиентский SOCKS5-прокси:
```powershell
dotnet run --project src\SimpleShadowsocks.Client -- 1080
```

После запуска клиент слушает `127.0.0.1:1080`.

### 3) Тесты

```powershell
dotnet test tests\SimpleShadowsocks.Client.Tests\SimpleShadowsocks.Client.Tests.csproj
```

## Что уже реализовано

- Инициализировано решение `.NET 9`.
- Реализован рабочий SOCKS5-сервер в клиенте:
- handshake `VER=5`, только метод `NO AUTH (0x00)`;
- поддержка `CONNECT (0x01)`;
- поддержка адресов `IPv4`, `IPv6`, `Domain`;
- корректные SOCKS5 reply-коды для ошибок;
- двунаправленный TCP relay (клиент <-> целевой хост).
- Добавлены unit-тесты SOCKS5 (5 сценариев), тесты проходят.
- Настроен общий вывод сборки всех проектов в корневые каталоги:
- `bin/<ProjectName>/...`
- `obj/<ProjectName>/...`

## Текущая структура проекта

- `src/SimpleShadowsocks.Client` - клиент, локальный SOCKS5-proxy.
- `src/SimpleShadowsocks.Server` - сервер туннеля (пока точка входа/каркас).
- `src/SimpleShadowsocks.Protocol` - общие типы и константы протокола.
- `tests/SimpleShadowsocks.Client.Tests` - unit-тесты SOCKS5-клиента.

## Архитектура (целевая)

### Компоненты

1. `Client (Local Proxy)`
- принимает локальные SOCKS5-подключения;
- формирует запросы к удаленному серверу по внутреннему протоколу.

2. `Server (Remote Relay)`
- принимает туннельные подключения от клиента;
- открывает исходящие TCP-соединения к целевым хостам;
- возвращает данные клиенту.

3. `Protocol (Shared Contract)`
- общий контракт между клиентом и сервером;
- версия протокола, типы кадров и адресов.

### Поток данных (цель)

1. Приложение -> локальный SOCKS5 (`Client`).
2. `Client` -> `Server` по TCP (позже: шифрованный канал).
3. `Server` -> целевой хост.
4. Ответ в обратном направлении.

## Внутренний протокол (черновой формат)

```text
+--------+----------+------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-----------+--------------+
```

Где:
- `VER` - версия;
- `TYPE` - `Connect/Data/Close/Ping/Pong`;
- `SESSION` - идентификатор логической сессии;
- `LEN` - длина payload;
- `PAYLOAD` - полезные данные.

## Ограничения текущего состояния

- Клиент уже реализует SOCKS5 и relay, но пока подключается к целевому хосту напрямую.
- Реальная передача через `Client <-> Server` туннель и шифрование еще не реализованы.

## Следующие шаги

1. Реализовать framing/serialization в `Protocol`.
2. Переключить клиент с прямого `CONNECT` на отправку `CONNECT/DATA/CLOSE` кадрами серверу.
3. Реализовать обработку сессий на сервере.
4. Добавить шифрование канала (MVP: pre-shared key + AEAD).
