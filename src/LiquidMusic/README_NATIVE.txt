====================================================
 Liquid Music — нативная версия (C# / .NET 8)
====================================================

1. УСТАНОВИ .NET 8 SDK:  https://dotnet.microsoft.com/download

2. СОБЕРИ (один раз), в папке проекта:
       dotnet build -c Release

3. ЗАПУСТИ приложение:
       bin\Release\net8.0-windows\LiquidMusicWidget.Native.exe
   или START_WIDGET.bat / START_WIDGET.vbs (запуск скрытым,
   иконка — в трее у часов, двойной клик — показать).

4. ВАЖНО: UI берётся из папки ui РЯДОМ С EXE (копируется сборкой).
   Если виджет выглядит по-старому — удали папку
   bin\Release\net8.0-windows\ui и пересобери.

5. ГОЛОС: положи vosk-model-small-ru-0.22 в
   models\vosk-model-small-ru-0.22 рядом с exe.

КАК ПОНЯТЬ, ЧТО ЗАПУЩЕНО ИМЕННО НОВОЕ ПРИЛОЖЕНИЕ:
 - в трее иконка с подписью "Liquid Music — C#";
 - подвал виджета НЕ показывает текст "Запустите виджет
   через START_WIDGET.bat" — в новом UI этого текста нет;
 - если бэкенд не подключён, подвал честно пишет
   "Бэкенд не найден. Запустите LiquidMusicWidget.Native.exe".

ПРОБЛЕМЫ: смотри logs\native.log и logs\native-startup.json
в папке приложения.
