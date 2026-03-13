# SimpleShadowsocks

Прототип клиент-серверного TCP-туннеля, похожего по идее на Shadowsocks:
- на клиенте поднимается локальный SOCKS5-сервер;
- клиент устанавливает TCP-сессию с удалённым сервером;
- сервер проксирует трафик к целевым хостам от имени клиента.

## Статус

Инициализировано `.NET 9` решение с 3 проектами:
- `src/SimpleShadowsocks.Client` - консольный клиент (локальный SOCKS5 endpoint);
- `src/SimpleShadowsocks.Server` - консольный сервер-туннель;
- `src/SimpleShadowsocks.Protocol` - общие модели и константы протокола.

## Архитектура

### 1) Компоненты

1. `Client (Local Proxy)`
- Слушает `127.0.0.1:1080` (по умолчанию).
- Выполняет SOCKS5 handshake с приложением пользователя.
- Извлекает `DST.ADDR`/`DST.PORT` из SOCKS5 `CONNECT`.
- Упаковывает запрос в внутренний протокол и шифрует полезную нагрузку.
- Передаёт кадры серверу по TCP.

2. `Server (Remote Relay)`
- Слушает порт туннеля (например, `:8388`).
- Аутентифицирует клиента и расшифровывает кадры.
- Для `CONNECT` создаёт исходящее TCP-подключение к целевому хосту.
- Для `DATA` пересылает байты в целевой сокет.
- Читает ответы от целевого хоста и отправляет обратно клиенту `DATA`-кадрами.

3. `Protocol (Shared Contract)`
- Версия протокола.
- Типы кадров (`Connect`, `Data`, `Close`, `Ping`, `Pong`).
- Типы адресов (`IPv4`, `Domain`, `IPv6`).
- DTO/record-структуры для межпроектного контракта.

### 2) Поток данных

1. Приложение -> SOCKS5 `Client` (локально).
2. `Client` -> `Server` (один или несколько TCP-каналов туннеля).
3. `Server` -> целевой ресурс (обычный TCP).
4. Ответ идёт в обратном направлении тем же маршрутом.

### 3) Модель сессий

- Каждому SOCKS5 `CONNECT` соответствует `sessionId`.
- Внутри одного TCP-туннеля можно мультиплексировать несколько `sessionId`.
- `Close` завершает конкретную сессию без разрыва всего туннеля.

### 4) Внутренний кадр протокола (предложение)

```text
+--------+----------+------------+-----------+--------------+
| VER(1) | TYPE(1)  | SESSION(4) | LEN(4)    | PAYLOAD(N)   |
+--------+----------+------------+-----------+--------------+
```

- `VER` - версия протокола.
- `TYPE` - тип кадра.
- `SESSION` - идентификатор логической TCP-сессии.
- `LEN` - длина payload.
- `PAYLOAD` - данные (`CONNECT` метаданные или сырой TCP data).

### 5) Безопасность (MVP -> Production)

MVP:
- pre-shared key;
- шифрование `AES-256-GCM` на payload/кадр;
- защита от replay через nonce + счётчик пакетов.

Дальше:
- ротация ключей;
- ограничение по IP/ACL;
- rate limiting;
- optional obfuscation/маскировка трафика.

### 6) Конфигурация

Рекомендуемый `appsettings.json` для клиента и сервера:
- `ListenHost`, `ListenPort`;
- `RemoteHost`, `RemotePort` (для клиента);
- `SharedKey`;
- `ConnectTimeoutMs`, `IdleTimeoutMs`;
- `MaxConcurrentSessions`.

## План реализации

1. Реализовать SOCKS5 handshake (`NO AUTH`, `CONNECT`) в клиенте.
2. Реализовать framing + сериализацию в `Protocol`.
3. Реализовать канал `Client <-> Server` с шифрованием.
4. Реализовать session manager и мультиплексирование.
5. Добавить heartbeat (`Ping/Pong`) и idle timeout.
6. Добавить интеграционные тесты (локальный echo-сервер).
7. Добавить метрики/логи (`Microsoft.Extensions.Logging`).

## Быстрый старт

```powershell
dotnet build SimpleShadowsocks.sln

dotnet run --project src/SimpleShadowsocks.Server -- 8388
dotnet run --project src/SimpleShadowsocks.Client -- 1080
```

Сейчас `Client` и `Server` являются каркасом с точками входа и базовым контрактом протокола.
