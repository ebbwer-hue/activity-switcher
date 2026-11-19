# Activity Switcher

**Автор проекта: Кушалиев Амир** · [Telegram: @foidslayer1038](https://t.me/foidslayer1038)

Приложение для Windows, которое показывает собственную карточку активности в Discord через локальный Rich Presence RPC.

**Стек:** C# · Windows Forms · .NET Framework 4.8 · Discord RPC.

## Возможности

- Выбор программы по EXE или ярлыку LNK.
- Название активности, описание, вторая строка и изображение.
- Показ карточки только при запуске выбранной программы.
- Пресет Visual Studio Code / Overflow.

## Сборка и запуск

1. Нужна Windows 10/11 с .NET Framework 4.8.
2. Запустите `build.ps1` в PowerShell.
3. Откройте полученный `ActivitySwitcher.exe`.
4. Создайте приложение в [Discord Developer Portal](https://discord.com/developers/applications) и укажите публичный Application ID.
5. Выберите программу и нажмите «Включить». Нужен запущенный настольный Discord.

Токен Discord и Client Secret не нужны. Приложение не запускает выбранную программу и не управляет карточками других приложений. Подробные настройки, ограничения и результаты исходных проверок — в `README.txt`.
