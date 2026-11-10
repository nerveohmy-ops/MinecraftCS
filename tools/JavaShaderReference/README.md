# Справочник по совместимости с Iris

В проверенном архиве Iris Fabric 1.11.7 для Minecraft 26.3 внутри
`META-INF/jars/` находятся три пригодных для отдельного инструментария JAR.

| Библиотека | Назначение |
| --- | --- |
| [GLSL Transformer](https://github.com/IrisShaders/glsl-transformer) | Синтаксическое дерево GLSL и преобразование исходников |
| [ANTLR 4 Runtime](https://github.com/antlr/antlr4) | Среда исполнения парсера GLSL Transformer |
| [JCPP](https://github.com/shevek/jcpp) | Препроцессор для директив, условий и макросов |

Повторный импорт из исходного архива пользователя:

```powershell
.\tools\JavaShaderReference\Import-IrisLibraries.ps1 -IrisJar "C:\Users\shaili\Downloads\iris-fabric-1.11.7+mc26.3.jar"
```

Импорт печатает SHA-256 и сохраняет оригинальные байты в локальном `lib/`.
Эта папка исключена из Git. Для дальнейшего использования библиотек в
Java-инструменте понадобятся JDK и условия лицензий соответствующих проектов.
Игровой рендерер MinecraftCS работает на .NET и не загружает эти JAR.

Вложенные JAR Sodium предоставляют интерфейсы Fabric для Minecraft. Рендерер
Iris опирается на клиент Minecraft, хуки Fabric и интеграцию Sodium. Разбор
GLSL позволяет преобразовать текст шейдера, а исполнение пакета требует
отдельной реализации стадий, буферов и параметров Minecraft в MinecraftCS.
[Исходники Iris](https://github.com/IrisShaders/Iris) · [Исходники Sodium](https://github.com/CaffeineMC/sodium)

## Что уже исполняет MinecraftCS

Положите папку пакета или ZIP в корневую папку `shaderpacks`, откройте
**Esc → Shaders** и нажмите **R**. Загрузчик разворачивает `#include` из пакета
и адаптирует часть классических входов GLSL для `gbuffers_terrain`.
Затем он выполняет однобуферные `deferred` и `composite` в числовом порядке
внутри каждой группы и завершает цепочку проходом `final`, если он есть. Каждая стадия читает предыдущий
цвет сцены через `colortex0` или `gcolor` и глубину через `depthtex0` или
`gdepthtex`. HUD рисуется поверх результата.

Оставшиеся зависимости пакетов включают карту теней, отдельные снимки глубины, дополнительные
цветовые буферы и их форматы, вычислительные стадии, раздельные проходы для
материалов и сущностей, данные мира и параметры из `shaders.properties`.
Эта реализация охватывает цепочки с одним цветовым буфером.
[Стадии Iris](https://shaders.properties/current/reference/programs/composite/) ·
[Буферы Iris](https://shaders.properties/current/reference/buffers/overview/)
