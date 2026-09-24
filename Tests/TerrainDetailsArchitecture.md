# Детализация террейна: согласованная архитектура

Статус на 23 сентября 2026: CPU-генератор, instanced backend, подгрузка вокруг камер,
ограниченный кэш матриц и объединение совместимых партий соседних ячеек.
Существующие материалы, геометрия, грязь и сцены автоматически не изменяются.
Проверки C# и CPU не заменяют проверку отрисовки в Unity/HDRP.

### Оптимизация под GTX 1660 — первый этап

Целевое оборудование выбрано пользователем: GTX 1660. Конкретные разрешение,
FPS и CPU пока не зафиксированы. Превосходство над Unity Terrain не измерено
и не гарантируется: нужен одинаковый визуальный результат и парный замер.

- LTWorld → Detail Renderer → Combine Draw Batches (по умолчанию включено):
  объединяет совместимые непрозрачные/alpha-test партии в пределах 2×2 ячеек.
  Лимит одной отправки — 511 матриц; не предполагает assumeuniformscaling.
  Ключ включает Mesh, Material, submesh, layer, renderingLayerMask,
  Cast/Receive Shadows, light/reflection probe usage и motion-vector mode.
  Прозрачные материалы (renderQueue >= 3000) остаются на прежнем пути.
- Отбор экземпляров, LOD, дистанции, плотность, маски, позиции и режимы
  теней не изменены. Объединяются уже отобранные части, включая нужные
  внекадровые кастеры. Bounds партии покрывают все её экземпляры с padding.
  HDRP probe/lighting и ветер всё равно требуют визуального A/B в Unity.
- Matrix Cache MiB (по умолчанию 16, 0 отключает): ограниченный CPU-кэш
  итоговых матриц частей. Одинаковые localMatrix внутри группы используют
  один массив. Заполняется в бюджете генерации; при нехватке памяти матрицы
  считаются прежним способом. Очистка/выгрузка возвращает бюджет, замена
  ячейки учитывается атомарно. Старые и строящиеся данные временно сосуществуют.
  Изменение лимита применяется при регенерации, не меняет позиции растений.
- Дополнительные CPU-буферы отправки переиспользуются: максимум 128×511
  матриц, примерно 4 МиБ плюс служебные структуры. При переполнении числа
  ключей накопленные партии отправляются, экземпляры не отбрасываются.
  Буферы очищаются между камерами, ссылки на старые recipes не удерживаются.
- Это всё ещё Graphics.RenderMeshInstanced с CPU-отбором и отправкой матриц.
  Постоянных GPU-буферов, indirect, GPU culling и occlusion culling пока нет.
  Следующий архитектурный этап — отдельный GPU backend для совместимых
  материалов с сохранением текущего пути для остальных. Нужна проверка
  HDRP depth/shadow/motion-vector проходов, probe-освещения и ветра.

Проверка первого этапа:

1. Дождаться окончания генерации, зафиксировать камеру и сбросить статистику.
   Записать «Партии: без объединения / факт», число инстансов, CPU render,
   отбор/отправку, GPU-время и полное время кадра. Первая цифра партий —
   расчёт для того же отбора, а не запуск прежнего рендерера и не GPU passes.
2. Выключить только Combine Draw Batches: число выбранных/отправленных частей
   и теневых частей должно совпасть, внешний вид — сохраниться. Сравнивать
   несколько кадров после прогрева, не одиночный пик. Меньше вызовов не
   гарантирует меньший GPU/frame time: это обязательно измерять.
3. Для полного старого CPU-пути дополнительно выставить Matrix Cache MiB=0,
   перестроить, дождаться готовности и повторить замер. Вернуть 16 для кэша.
4. Проверить LOD, Off/On/TwoSided/ShadowsOnly, порог дальности теней,
   Scene/Game/Reflection камеры, ветер, прозрачный материал, движение через
   границы ячеек и повторные rebuild/disable без накопления памяти.
5. Финальное сравнение на GTX 1660 делать в standalone с одной Game-камерой,
   одинаковыми resolution, HDRP quality, VSync/FPS cap, mesh/material, LOD,
   плотностью, дальностями и тенями. Для сравнения с Unity Terrain также
   выровнять фактически видимую нагрузку. Записывать CPU/GPU и frame-time
   распределение; сами счётчики draw calls не доказывают превосходство.

CPU-тесты очереди проверяют сохранение всех экземпляров и bounds, независимость
всех полей ключа, предел памяти, переполнение и смену камер. Синтетический
пример объединяет 80 небольших партий в 16 с теми же 8000 экземпляров —
это проверка алгоритма, не результат на сцене пользователя или GTX 1660.

### Запуск и текущие ограничения

Короткий профиль CPU/GPU (23 сентября 2026):

- В Detail Renderer есть кнопка «Записать профиль CPU/GPU…»; альтернативно
  Tools → Local Terrain → Detail Profile Capture. Нужен Play Mode, активная
  Game-камера и полностью загруженная детализация. Для исходного замера
  Combine Draw Batches выключить вручную. Инструмент его не переключает.
- Остановить текущую запись Profiler, выключить Deep Profile, выбрать
  текущий Editor / Play Mode в Target. Назначить renderer и камеру, нажать
  «Записать профиль — 10 секунд». Через 2 секунды подготовки включаются
  запись и CPU/GPU-модули; через 10 секунд запись останавливается.
- Выход из Play Mode, пауза, закрытие окна или перекомпиляция
  останавливают запись. Свои настройки логирования и модулей восстанавливаются;
  после переключения Target настройки нового удалённого Player не меняются.
  Вход в Play Mode, настройки материалов, света, VSync, FPS cap и Scene View
  инструмент не изменяет. История Profiler не очищается.
  Фоновый проход Auto Refresh не обрывает запись: он проверяет и неизменённые
  ячейки. Его активность отмечается в описании и видна в LT.Details.Generate.
- Уникальные .raw и .txt сохраняются в Logs/DetailProfiles. В .txt — аппаратная
  конфигурация, настройки рендерера, причина остановки, число игровых кадров
  и предупреждения об изменении камеры/числа экземпляров. Это не CSV замеров.
  GPU-модуль запрашивается, но отсутствие GPU-данных не выдаётся за 0 мс.
  Запись включает другие активные камеры; это ещё не standalone benchmark.
- В Profiler загрузить .raw через Load. В CPU Timeline искать LT.Details.Submit
  и вложенный LT.Details.GraphicsSubmit. Второй охватывает только вызов
  Graphics.RenderMeshInstanced, включая возможные внутренние ожидания,
  а не работу GPU. Остальная часть Submit включает подготовку, очередь,
  bounds и вспомогательные операции. В GPU Usage смотреть доступные проходы
  отдельно. Эти CPU-времена не складывать с GPU-временем как последовательные.
- Официальные API/ограничения: [binary logging](https://docs.unity.com/en-us/engine/6000.7/script-reference/unityengine/profiling/profiler/enablebinarylog),
  [GPU Usage](https://docs.unity3d.com/6000.0/Documentation/Manual/ProfilerGPU.html).
  Сборка проверяет API установленного Unity 6000.3; native-запись и визуальный
  разбор реальной сцены требуют запуска пользователем. Автотесты здесь проверяют
  только source contracts защит/очистки и наличие маркера.

Общая маска плотности слоя/Detail Stamp:

- Над списком объектов расположен всегда раскрытый блок «Маска плотности
  детализации». «Включить общую маску» включает процедурные пятна
  для всех наследующих объектов (по умолчанию выключено).
- У каждой записи режим «Общая / Своя / Без маски». Новые записи наследуют
  общую маску; свои параметры показываются только в режиме «Своя».
  Штамп использует свою общую маску для собственного набора Add/Replace,
  не наследует одновременно маски разрешённых слоёв.
- Старые включённые индивидуальные маски мигрируют в «Своя» с сохранением
  параметров. Старые выключенные — в «Общая»; общая по умолчанию выключена.
  Кнопка «Все объекты → общая маска» переводит список одним действием,
  сохраняет собственные параметры для возврата и поддерживает обычный Undo.
- Patch Size — характерный масштаб, м; Coverage — заполненность через порог
  шума (не точный процент площади); Softness — мягкость края.
- Minimum Density — остаточная доля плотности в проплешинах; Seed — рисунок.
- Начальная настройка: Size=6, Coverage=0.6, Softness=0.2, Minimum=0.05.
- Двухмасштабный непрерывный value noise считается по локальным XZ террейна.
  Нет текстуры/RT, зависимости от камеры, чанка, времени или ячейки стриминга.
  Мировой seed (и seed штампа для его записей) участвует в рисунке; ID префаба
  не участвует, поэтому одинаковые настройки могут дать общие пятна нескольким видам.
- Маска умножает вероятность принятия после весов слоёв/штампов и не двигает
  кандидатов. Менять Patch Seed можно без перестановки оставшихся кустов.
  Выключение возвращает исходное распределение.
- В Replace/Remove действие штампа по-прежнему определяется его областью
  и разрешёнными слоями; маска записи прореживает только её собственные объекты.
- Параметры общей маски скрыты, пока она выключена;
  подсказки есть на всех полях. Auto Refresh подхватывает изменения; при
  ручном режиме используйте «Перестроить детализацию».
- Под параметрами включённой общей маски есть квадратное превью 160 px
  (временная текстура 128²). Белое — множитель 1, чёрное — 0, серое — промежуточный.
  Показана итоговая маска плотности, не сырой шум и не готовые позиции объектов.
  Образец начинается в XZ=(0,0), без покрытия слоёв и границ штампа.
- «Область превью, м» меняет только масштаб образца. У слоя «Seed мира
  (только превью)» по умолчанию 12345: для совпадения с нужным миром укажите
  seed его Detail Renderer. У штампа с настроенным миром seed берётся из
  рендерера и текущего seed штампа автоматически.
- Предпросмотр читает текущие SerializedProperty, обновляется при правках
  и Undo, использует ту же LTDetailMath.DensityMask, что и генерация.
  Кэш ограничен восемью временными текстурами с очисткой по бездействию,
  перезагрузке сборок и закрытию Editor. Assets/сцены превью не создаёт.

1. Добавьте Local Terrain / Detail Renderer на тот же объект, где LTWorld.
   Нужны включённая покраска слоёв и сгенерированные чанки.
2. Заполните список детализации слоя. На материалах префабов включите
   Enable GPU Instancing; рендерер не изменяет assets автоматически.
3. Для локальных изменений добавьте Detail Stamp, назначьте World и слои
   либо All Layers. Поворот штампа поддерживается вокруг Y, без наклона X/Z.
4. Начните с небольшой плотности. Status в Detail Renderer показывает
   число экземпляров и причину отказа. После исправления ошибки нажмите
   «Перестроить детализацию». Есть автоматическое обновление раз в секунду.

- Расстановка на исходной полной геометрии, исключая отверстия; выбирается
  верхняя поверхность. GPU displacement, грязь и вертикальные стенки не являются
  отдельными поверхностями спавна. Height Offset позволяет заглубить основание.
- Плотность задаётся на м² проекции XZ. LTWorld: Rotation=0, Scale=1.
- Итоговые веса: те же endpoint-карты покраски и формула Height Blend,
  при трипланаре — сумма трёх проекций с весами normal^4. Height читается
  из mip 0 для независимости расстановки от камеры (не экранный mip шейдера).
- Кандидаты на независимой метровой решётке, отдельный hash-канал плотности:
  изменение размера ячеек не перетасовывает объекты; рост плотности добавляет.
- Add/Replace/Remove применяются по priority/ID с фильтром категорий и
  весом разрешённых слоёв. Дублирующиеся ID блокируют генерацию с подсказкой.
- Обновляется готовая ячейка целиком; остальные продолжают рисоваться.
  Перенос штампа инвалидирует старую и новую область. Изменение покраски
  инвалидирует ячейки затронутого чанка. Подготовка геометрии/снимков карт
  синхронная; только перебор кандидатов разбит по мягкому бюджету времени.
  Проверка новых внешних изменений выполняется после завершения текущего прохода.
- Ячейки планируются только в XZ-окрестностях активных Game-камер и открытых
  Scene View (если включён Scene View). Reflection/Preview не инициируют подгрузку;
  отражения рисуют уже загруженные объекты. Радиус — Max Distance + Preload Distance.
- План обновляется раз в 0.25 с, включая поворот камеры без перемещения.
  Сначала незагруженные ячейки: видимые в пределах Max Distance, затем
  остальные в этой дистанции, затем запас предзагрузки. Внутри группы —
  ближайшие по XZ. Уже загруженные ячейки проверяются после незагруженных.
  Для нескольких камер выбирается лучший приоритет с расстоянием именно
  до соответствующей камеры; равенство разрешается координатами ячейки.
- Видимость для очереди оценивается по frustum и консервативным bounds:
  у готовых ячеек — bounds объектов, у новых — XZ ячейки и общий диапазон
  высот мешей террейна. Это приближение только для очереди, не occlusion
  culling и не новый запрет отрисовки. Perspective/Orthographic поддержаны.
- Следующая ячейка выбирается по актуальному плану. При смене области
  приоритет обновляется без сброса нужной незавершённой ячейки: текущая
  достраивается целиком, поэтому большой Cell Size всё ещё может задержать
  появление следующей. Работа над больше не нужной ячейкой отменяется на
  очередной проверке бюджета. Новые территории вне снимка подготовки
  переходят в следующий проход со свежими картами, а не становятся пустыми
  ячейками по старому снимку. Бюджет генерации и стабильные позиции не меняются.
- CPU-тесты очереди: видимое/за кадром/предзагрузка, пропуски перед кэшем,
  поворот, несколько камер, стабильные равенства, высота и перенос bounds,
  граница frustum. Сборка и тесты не заменяют проверку задержек в Unity.
- Unload Delay (по умолчанию 5 с) удерживает дальний кэш; при нехватке Max Cells
  или Max Instances этот кэш освобождается раньше. Нужные камерам ячейки ради
  бюджета скрыто не выбрасываются: превышение показывается как ошибка.
- Max Cells ограничивает загруженные ячейки, а не полный размер мира.
  При Cell Size=16 для мира 4096² не требуется создавать все 65536 ячеек.
  Изменение Cell Size сбрасывает кэш, но не меняет стабильные позиции.
- При возвращении ячейка восстанавливается по тем же координатам/seed.
  Дополнительный CPU-кэш геометрии и coverage для детализации ограничен
  пересекающими зону чанками; штатные ресурсы покраски/террейна не выгружаются.
  Маски материалов остаются общими текстурами, не разрезаются по ячейкам.
- Лимиты ячеек, экземпляров и кандидатов явные. При превышении остаются
  готовые ячейки; статус сообщает об остановке. Это не точный лимит в байтах:
  учитывайте временную строящуюся ячейку и подготовку данных чанков.
  После телепортации новая область появляется постепенно, не мгновенно.
- CPU distance/frustum culling, instancing партиями до 511 матриц,
  по ячейке/записи/LOD/части. Между разными записями партии пока не объединяются.
  Offscreen shadowcasters сохраняются до дистанционного отсечения, чтобы
  camera frustum не обрезал их тени. Это консервативный, более дорогой вариант.
- Порог LODGroup учитывает perspective/orthographic, lodBias и maximumLODLevel.
  Без LODGroup действует Cull Distance. Thin In Distance использует стабильный hash.
- Shader Fade и LOD crossfade пока дискретные, с предупреждением. Реакция
  на игрока/машину и indirect/GPU culling не реализованы.
- Настройки теней, rendering layers, обычных probes и motion vectors передаются
  из частей префаба. Proxy Volume/Custom Provided probes отклоняются.
  Ветер, APV, reflection probes, motion vectors и HDRP shader variants нужно
  проверить в сцене. Истории движения инстансов при перестройке пока нет.
- Для камер используется beginCameraRendering перед culling HDRP; отдельная
  фильтрация/LOD на камеру. Preview-камеры исключены, Scene View опционален.
  Статистика «инстансы частей» считает draw-части, а не уникальные растения.
- При выключении компонента CPU-экземпляры и его снимки карт освобождаются.
  В Player после программного изменения структуры префаба вызовите Rebuild().

### CPU-диагностика и раннее отсечение детализации

- В Detail Renderer блок «Диагностика детализации — CPU»: последний Tick
  и его пик, планирование, последняя подготовка карт/мешей (включает
  UpdatePainting), порция генерации и CPU-отрисовка последней камеры.
  Подготовка сохраняет последнее измерение до следующего запуска; генерация
  показывает 0 в Tick без генерации. Пики сбрасываются отдельной кнопкой,
  не требующей перестройки. После прогрева/генерации сбросить пики для замера.
- Время отрисовки включает отбор и отправку партий. Строка «Отбор / отправка»
  разделяет эту же величину, а не добавляет ещё время. Это elapsed-время
  CPU-вызовов с возможными ожиданиями, НЕ GPU frame time, не весь кадр Unity
  и не сумма всех камер. Последняя камера подписана; пик может быть от другой.
- Счётчики одного прохода: ячейки проверено/отсечено; экземпляры проверено
  поштучно/выбрано; всего отсечено/из них заранее ячейками и группами.
  Для одного снимка resident-состояния: проверено + заранее = загружено;
  выбрано + всего отсечено = загружено. Отправленные части считаются отдельно:
  один куст с несколькими submesh увеличивает этот счётчик несколько раз.
  «Частей с обычными тенями» — отправленные кастеры, не каскады/теневые draw
  calls HDRP. GPU и контактные тени этими счётчиками не измеряются.
- Unity Profiler: LT.Details.Tick, Streaming, Prepare, Generate, RenderCPU,
  Submit. Области вложены, их длительности нельзя суммировать повторно.
  Таймеры используют GetTimestamp без создания Stopwatch на каждый Tick.
- При генерации сохраняются bounds мешей и отдельные bounds позиций
  оснований для ячейки/группы, а также максимальная дальность её кастеров
  с учётом Cull Distance и ограничения обычных теней. Это разные bounds:
  mesh в префабе может быть смещён относительно pivot. Закадровая ячейка
  пропускается до перебора экземпляров только если ни один её кастер не
  может пройти дистанционный тест. То же отсечение применяется к группам.
  Близкие закадровые кастеры сохраняются; это не occlusion culling.
- Расстояние, видимость и разрешение теней вычисляются один раз на экземпляр
  и камеру. Затем части мешей используют сохранённые флаги, без повторных
  тестов frustum/дистанции. ScreenHeight считается только при LODGroup;
  sqrt нужен только для включённого Thin In Distance. Сохраняются camera
  layer masks и режимы Off/On/TwoSided/ShadowsOnly, LOD и параметры префаба.
  Submit использует bounds группы вместо всей ячейки, всё ещё с запасом
  на деформацию и со всеми LOD. Материалы и плотности не изменяются.
- Проверки: таблица всех shadow modes × видимость × дистанция, границы
  дистанций, смещённые меши, 2000 смешанных наборов групп/камер с сравнением
  раннего отсечения с прежним поштучным решением. CPU/source проверки не
  заменяют проверку HDRP на GPU и замер FPS в проекте.
- Ручной тест: дождаться «Готово», сбросить статистику, сравнить взгляд вдаль,
  вниз и от травы без изменения плотности/дальностей. Проверить тени от
  ближайших растений за краем кадра, порог Shadow Distance, несколько камер
  и LOD. Для общей производительности сравнивать также Player/Profiler;
  открытый инспектор Scene View сам добавляет редакторскую нагрузку.

### Что можно настроить сейчас

- В инспекторе LTSurfaceLayer: «Объекты детализации», параметры записей,
  кнопка «Проверить префабы детализации»; результат проверки в Console.
- В слое и Detail Stamp есть область массового drag-and-drop префабов из
  Project: новые записи со стандартными значениями, пропуск дубликатов,
  Undo/Redo. Объекты сцены и неподходящие файлы пропускаются.
  Записи подписаны именами префабов; все параметры имеют русские подсказки.
- Add Component / Local Terrain / Detail Stamp: область, World, разрешённые
  слои, режим, категории, seed, приоритет и собственный список объектов.
- Выбранный штамп показывает зелёный контур; набор рисуется через Detail Renderer.
- ID записей сохраняются при перестановке списка, копия записи получает новый ID.
  Одинаковые ID скопированных штампов пока обнаруживаются инспектором:
  кнопка «Новый ID этой копии» исправляет конфликт с Undo. Автоматическая
  обработка duplication/prefab instances ещё предстоит перед генерацией.
- Разбор префаба сохраняет части/submesh, относительные transforms, root scale,
  пороги LOD и настройки Renderer. Коллайдеры/скрипты не инстанцируются.
  Неподдерживаемые и неоднозначные структуры диагностируются, материалы не меняются.
- Математика стабильных кандидатов и масок штампа покрыта CPU-тестами.
  Конкретные prefab assets и инспекторы нужно проверить в Unity.

## Область первого этапа

Массовая статическая детализация: трава, цветы, веточки, мелкие камни.
Настройки объектов непосредственно в LTSurfaceLayer. Отдельный профиль-asset
не обязателен; переиспользуемые профили можно добавить позже.
Префаб — источник мешей, материалов, LOD и настроек Renderer, а не шаблон
для создания GameObject на каждый экземпляр. Скрипты, коллайдеры,
SkinnedMeshRenderer и интерактивное поведение в первый этап не входят.

## Авторские настройки

### Запись объекта в слое

- Постоянный идентификатор записи; перестановка списка не меняет расстановку.
- Префаб, категория (растительность / камни / другое), включение.
- Плотность на квадратный метр и вероятность появления.
- Диапазоны масштаба, поворота вокруг нормали, выравнивание по поверхности.
- Диапазон уклона и допустимое смещение по высоте.
- Дистанции начала исчезновения и полного отсечения.
- Опциональное уменьшение плотности вдали (прежде всего для травы).
- По умолчанию тени наследуются из каждого Renderer каждого LOD.

### LTWorld

- Общий seed, множитель плотности, предел дистанции видимости.
- Размер ячейки видимости, независимый от размера геометрического чанка.
- Бюджет генерации за кадр и явные ограничения памяти/числа экземпляров.
- Необязательный общий предел дистанции теней; значение «наследовать»
  сохраняет поведение префабов. Такой предел никогда не включает тени
  у Renderer, где Cast Shadows = Off.
- Диагностика: ячейки, кандидаты, видимые экземпляры по LOD, партии,
  теневые партии, время генерации/отсечения, объём буферов.

Численные значения по умолчанию подбираются на тестовой сцене. Пока нет
обоснованного обещания конкретного FPS, размера ячейки или числа объектов.

## Штамп детализации

Отдельный компонент, не изменяющий покраску и материал террейна.

- Круг / прямоугольник, размер, поворот, мягкость края.
- Явный список разрешённых слоёв. Пустой список не означает «все»:
  для этого нужен отдельный флаг, чтобы избежать случайного массового спавна.
- Категории, на которые действует штамп.
- Режимы: добавить / заменить / убрать; локальный набор объектов,
  множитель плотности, seed, приоритет.
- Постоянный ID штампа для стабильной расстановки и разрешения равного
  приоритета. Дублирование штампа должно создавать новый ID.

Вес действия = маска области × итоговое покрытие разрешённых слоёв.
Сумма весов выбранных слоёв ограничивается диапазоном 0..1.
Использовать согласованные с материалом правила смешивания, включая Height;
не заменять итоговые веса одним индексом доминирующего слоя.

«Убрать» подавляет выбранные категории. «Заменить» подавляет их базовый
набор и добавляет набор штампа с плавным переходом по весу. «Добавить»
сохраняет предыдущий набор. Штампы применяются по возрастанию приоритета,
затем по стабильному ID. Определить единый порядок в генераторе и диагностике.

## Разделение генерации и отрисовки

1. Источник поверхности: высота, нормаль, наличие поверхности/отверстия,
   итоговые веса слоёв. Брать исходную полную геометрию, не текущий render LOD.
2. Генератор: постоянные кандидаты и экземпляры, сгруппированные по ячейкам.
3. Описание префаба: LOD, части мешей, submesh, материалы, transforms,
   bounds и свойства Renderer.
4. Отсечение и выбор LOD: отдельные результаты для каждой камеры и теней.
5. Рендерер: получает готовые описания и экземпляры; не генерирует позиции.

Хранилище экземпляров не зависит от CPU/GPU-реализации отсечения.
Первый backend — CPU-отсечение + instanced draw; позднее GPU culling и indirect
используют те же ID, трансформации и настройки, без изменения авторских данных.
Indirect-rendering и GPU-отсечение — предусмотренный следующий этап,
а не уже реализованная возможность.

## Стабильная генерация и локальные изменения

- Seed кандидата: seed мира + ID записи/штампа + координаты генерационной
  ячейки + номер кандидата. Не использовать InstanceID, порядок обхода
  Dictionary, текущее время или нестабильный string.GetHashCode.
- Размер генерационной решётки отделить от размера ячеек видимости:
  настройка batching не должна перетасовывать растения.
- Изменение плотности меняет порог принятия/число кандидатов, но не позиции
  уже принятых кандидатов. Для разных случайных свойств — разные hash-каналы.
- Отверстия исключают спавн; уклон и вес слоя фильтруют кандидатов.
- Перемещение камеры не перегенерирует мир и не загружает все матрицы заново.
- При перемещении штампа инвалидируются объединённые старая и новая области.
- Изменение слоя инвалидирует только использующие его ячейки; геометрии —
  затронутую область; префаба — его описание и зависимые bounds/партии.
- Перестроение выполняется в бюджете с последующей заменой готовых данных.
  Не оставлять частично обновлённые массивы доступными отрисовке.

## Ячейки, bounds и LOD

- Сначала отсекать ячейки, затем экземпляры внутри видимых ячеек.
- Bounds учитывают все части префаба, максимальный масштаб, повороты,
  все LOD и запас для ветра/вершинной анимации. Не ограничивать bounds
  положением корня префаба или плоской площадью террейна.
- Сначала применять дешёвое дистанционное отсечение, затем frustum и LOD.
- LODGroup — описание, а не работающий компонент у каждого инстанса.
  Учитывать screen-relative пороги, размер bounds, параметры камеры и LOD bias;
  предусмотреть perspective и orthographic камеры.
- Без LODGroup использовать один LOD с независимой дистанцией исчезновения.
- Иерархические local transforms MeshRenderer сохранять относительно корня.
  Поддержать несколько Renderer и несколько submesh на одном уровне.
- Вложенные LODGroup и неподдерживаемые Renderer выявлять валидатором;
  не дублировать их скрытно и не терять части без предупреждения.
- Crossfade/dither и плавное исчезновение доступны только с подтверждённой
  поддержкой материала. Иначе — явное дискретное переключение/отсечение.
- Несколько камер, SceneView и отражения не должны разделять один перезаписываемый
  список видимых экземпляров. Выбор поддерживаемых проходов проверить в HDRP.

## Группировка и материалы

Ключ партии включает mesh, submesh, shared material, LOD, ShadowCastingMode,
Receive Shadows и остальные влияющие на проход параметры (layer/rendering
layer, режим освещения/probes). Одинаковый префаб ещё не гарантирует одну партию.

- Не создавать копию материала на каждый экземпляр.
- Cast Shadows = Off / On / TwoSided / ShadowsOnly сохранять отдельно для
  каждого Renderer каждого LOD; Receive Shadows зависит также от шейдера.
- Не изменять импортированные материалы/префабы молча ради instancing.
  Совместимость материалов проверять и показывать автору понятную диагностику.
- Разбивать draw-партии по ограничениям выбранного Unity API и layout буферов.
- Повторно использовать CPU-массивы и GPU-буферы, не создавать мусор каждый кадр.
- Освещение HDRP, light probes, motion vectors и ветер проверить отдельно;
  не обещать полного совпадения с обычным Renderer без тестов.

## Тени, дальняя плотность и стоимость травы

- Camera-frustum culling нельзя безусловно использовать для теневых списков:
  объект вне кадра может отбрасывать видимую тень. Требуется отдельная
  консервативная область shadow-caster или корректный путь HDRP.
- Shadow-only объекты не должны исчезать из теневого прохода из-за отсутствия
  обычного цветового draw. Off никогда не попадает в shadow draw.
- Дальняя плотность выбирает стабильное подмножество по ID экземпляра:
  никакой новой случайной выборки каждый кадр. Для камней выключена по умолчанию.
- Без shader fade смена подмножества может быть заметной; не называть её плавной.
- Instancing не устраняет overdraw. Проверять пустые области alpha-card,
  стоимость alpha clip/ветра/освещения и теней на GPU, а не только draw calls.

## Последовательность реализации и критерии приёмки

### Будущее взаимодействие с игроком и машиной

- Общий компонент-источник взаимодействия: капсула игрока, контактные пятна
  колёс или явно заданные формы машины. Не создавать коллайдер/GameObject
  на каждый экземпляр растительности.
- Разделить мгновенный изгиб рядом с источником и примятие с памятью следа.
  Для второго предусмотреть локальную карту направления/силы примятия и
  восстановление по времени. Она независима от карты глубины грязи:
  трава может приминаться без деформации земли.
- Колёса записывают примятие только при подтверждённом контакте. Прогноз
  движения может готовить ресурсы/видимость, но не оставляет след заранее.
  Тип контроллера машины ещё не выбран: источник не привязывать исключительно
  к WheelCollider, адаптер контактов отделить от системы растительности.
- Настройки реакции по типу растения: сила изгиба, восстановление, маска
  закреплённых корней/подвижных вершин. Камни по умолчанию не реагируют.
- Это визуальная деформация в совместимом шейдере, не физика ветвей и не
  изменение коллайдеров. Для ветвей требуется подготовка меша/масок изгиба.
- Общие координаты и данные взаимодействия между LOD; согласовать основной,
  теневой, depth и motion-vector проходы, включая предыдущее состояние.
  Bounds должны включать максимальное отклонение и ветер.
- Ограничить число мгновенных источников и память карт, отсекать источники
  по ячейкам. Не перебирать всех игроков/колёса на каждой вершине без бюджета.
- Не включать этот этап автоматически в первоначальный спавнер. Сейчас
  сохраняется архитектурная совместимость; реализация и GPU-проверки позже.

1. Модель данных в слоях/штампах, стабильные ID, валидатор префабов.
2. Детерминированная генерация, итоговые веса, отверстия и локальная инвалидация.
3. Ячейки, постоянные буферы, CPU culling, instancing, LOD и дистанции.
4. Проверка теневых режимов, нескольких камер, HDRP-материалов и границ ячеек.
5. Профилирование травы и камней; только затем GPU culling/indirect при
   подтверждённом CPU bottleneck. Occlusion culling — отдельная будущая задача.

Автотесты: повторяемость seed; сохранение существующих позиций при смене
плотности; независимость от порядка списка; веса границ; stamp priorities;
инвалидация старой/новой области; LOD без группы; ключи теневых партий.

Unity-проверки: LOD с несколькими Renderer/submesh; prefab transforms;
Cast Shadows Off/TwoSided/ShadowsOnly; тень объекта вне кадра; камера на
границе ячейки; две камеры; отключение/удаление мира без утечки буферов;
многократное редактирование штампа без роста памяти. Замеры CPU и GPU
проводить отдельно, в фиксированной сцене и на целевом оборудовании.

## Дальность обычных теней детализации

- Слой/Detail Stamp → Объекты детализации → запись: «Ограничить дальность
  теней», затем «Дальность теней, м» (25 м по умолчанию). Ограничение изначально
  выключено, поэтому старые записи наследуют поведение префаба без изменений.
- Отсечение отбрасываемых обычных теней дискретное, по расстоянию от текущей
  камеры до основания экземпляра, отдельно от Cull Distance. 0 выключает
  обычные тени на любой дистанции. Receive Shadows не меняется.
- На каждой камере/LOD/части меша инстансы разделяются на две партии: исходный
  ShadowCastingMode и Off. Одна ячейка может содержать обе партии. Off из
  префаба никогда не включается; TwoSided сохраняется вблизи; ShadowsOnly
  после отсечения не превращается в видимую геометрию. Вне кадра сохраняются
  только действующие кастеры. Пересечение порога может добавить draw call,
  но дальняя трава больше не рисуется в обычные карты теней.
- Контактные тени не включаются этим параметром: нужны Use Contact Shadows
  в HDRP Asset, Contact Shadows во Frame Settings, включённый override
  Contact Shadows в Volume и Contact Shadows у источника света. Материал
  травы должен писать глубину. Это экранный эффект с дистанциями из Volume,
  поэтому он не гарантирует замену обычных теней для травы вне кадра/вдали.
- Проверено по документации установленного HDRP: Cast Shadows Off не
  исключает видимую геометрию из экранных контактных теней. Материалы, свет
  и Volume код не изменяет. Нужна визуальная проверка в Unity: трава по обе
  стороны порога в одной ячейке, LOD, Scene/Game камеры, Off/TwoSided/
  ShadowsOnly, контактные тени включены и выключены. CPU-тесты покрывают
  порог, нулевую дистанцию, старые значения и разбиение партий; не GPU.

## Static instance motion history (2026-09-24)

- User A/B in Play Mode confirmed that explicit previous matrices remove the
  observed stationary-foliage flicker. Both merged and unmerged submissions now
  always use `LTDetailMotionData`: `prevObjectToWorld = objectToWorld`.
- The temporary nonserialized diagnostic toggle and matrix-only fallback were
  removed. No scene migration, material changes or manual enabling is needed.
- One reusable 511-element CPU scratch array holds two matrices per instance
  (65,408 bytes of payload), allocated on first submission and released by Clear.
  Counts, ordering, bounds, LOD, shadow partitions and inherited motion mode stay
  unchanged. The additional matrix data has not been performance-profiled.
- This models static root transforms. Camera motion is still handled by HDRP;
  vertex deformation history is still the shader's responsibility. Future moving
  instances need actual previous transforms keyed by stable identity, not batch
  slot. Rebuilt/new instances currently start with zero transform motion.
- This does not enable `_ADD_PRECOMPUTED_VELOCITY` or change material motion
  passes. General wind/deformation correctness is not established by this test.
- CPU checks cover layout, full/partial/reordered batches and mandatory use of
  the shared submission path; they do not substitute for GPU validation.

## Opt-in GPU details (2026-09-24)

- LTWorld → Detail Renderer → **Gpu Details**. Default off, no scene migration.
  **Gpu Memory MiB** defaults to 128 and caps this world's instance, per-camera
  visible-ID and indirect-argument buffers. Unsupported groups remain on CPU.
- CPU generation/streaming and coarse cell/group rejection remain. Static root
  transforms/bounds are uploaded once per group. Compute selects distance,
  frustum, deterministic thinning and LOD, retaining offscreen shadow casters;
  append counters drive RenderMeshIndirect without CPU readback. This is not
  Hi-Z/occlusion culling, and does not reduce material/alpha-test shading cost.
- Per-camera output buffers prevent Scene/Game from overwriting deferred draw
  inputs. They expire after five seconds of non-use; group replacement, unload,
  rebuild, disable and CPU-mode switch release their resources. Upload preparation
  is now incremental (see below); generation is not GPU-based or asynchronous.
- Source graph/material assets are never rewritten. GrassWind.ltdetailshader
  imports an indirect shader copy of DA_Grass_WIND with its graph dependencies
  and texture defaults. BaseProps and HdrpLit now adapt the two audited raw
  shaders used by stones and driftwood (see expansion below). Runtime uses material clones. Other shader families,
  transparent materials, more than eight LODs, XR and Custom/LPPV probes fall
  back to the established CPU path. Initial backend supports Windows D3D11/12.
- The adapter retains hidden material subassets for keyword combinations found
  in project .mat assets, refreshed before player builds. This prevents relying
  solely on runtime clones for shader_feature preservation. New keyword sets
  created only at runtime (without a matching project material) are not covered.
  A packaged player build still requires separate validation.
- Individual classic Light Probe SH data is not uploaded (LightProbeUsage.Off).
  Compare illumination before adopting this mode. APV/other HDRP lighting must
  be checked in the real scene; equivalent lighting is not yet guaranteed.
- Root motion history uses the same source transform for current/previous,
  independently of append order; camera motion remains HDRP's responsibility.
  Wind history still belongs to the source graph. Add Precomputed Velocity must
  remain off; enabling it makes that group use CPU fallback, not a mesh fix.
- Inspector GPU calls/candidates are separate from CPU counters. Candidates
  are not visible counts; CPU selection counters exclude GPU-handled groups.
  Total CPU rendering time includes GPU dispatch/submission, NOT GPU time.
- Tools → Local Terrain → Check GPU Details compiles indirect raster passes
  and runs small compute tests (65 instances, frustum, offscreen shadows,
  shadow-distance partitions, ShadowsOnly, LOD and range). Its synchronous
  readback is validation-only; report: Logs/DetailProfiles/gpu-details-check.txt.
- Acceptance still requires fixed-camera CPU/GPU A/B after streaming settles:
  motion vectors while stationary/moving, near/far LOD, shadows outside frame,
  two cameras, material lighting and repeated streaming/toggling. Measure CPU
  and GPU separately in a player on GTX 1660. No FPS superiority over Unity
  Terrain is implied by this implementation or by a lower draw-call count.

### Verification performed for this GPU iteration

- C# editor/runtime compilation: zero errors. TerrainBridgeChecks: passed,
  including 192-byte GPU ABI, source contracts and the static motion tests.
- Isolated Unity 6000.3.23f1, HDRP 17.3, RTX 5070: nine procedural raster
  passes compiled on D3D11 and D3D12. A deliberate error inside the test copy's
  UNITY_PROCEDURAL_INSTANCING_ENABLED branch was detected, then removed; this
  verifies the check is not merely compiling the fallback variant.
- Native compute tests passed: frustum, 65-thread boundary, offscreen casters,
  shadow partitions, ShadowsOnly, perspective/orthographic LOD, quality clamp,
  deterministic thinning and range. The extended set and retained material
  subasset were checked on D3D12. Reports are in Logs/DetailProfiles/
  gpu-details-validation-d3d11-20260924.txt and
  gpu-details-validation-d3d12-20260924.txt.
- Actual scene appearance, player build, GTX 1660 performance and long-running
  GPU resource churn have NOT been validated by these tests.

## Incremental GPU upload and ownership accounting

- Detail Renderer → Gpu Upload Milliseconds (default 1 ms) and Gpu Upload MiB
  (default 2 MiB). One quota per world/game frame, or per editor update outside
  Play Mode. Begin for a second camera does not reset it. Different worlds have
  separate budgets. No camera/scene/material assets are changed.
- Root data is prepared and SetData'd in contiguous chunks of up to 2048 records
  using one reusable 384 KiB CPU staging array. A large group therefore no longer
  builds a full temporary matrix array in a single camera callback. View outputs
  are prepared one draw at a time, with at most eight allocation units per frame
  (a root buffer, or draw args plus a visible buffer only if not already shared). Driver calls are indivisible: the time
  limit is soft and does not guarantee a maximum frame duration.
- GPU rendering begins only after the complete root and that camera's complete
  view are ready. Until then the unchanged CPU renderer draws the whole group;
  no partial GPU draws, duplicate plants or hidden density reduction. Temporary
  CPU cost is possible during this handoff. Existing ready groups remain on GPU.
- Memory preflight reserves the full intended root/view sizes, including pending
  allocations. Resident bytes count actual buffer payloads; reserved bytes may
  be larger. Partial allocations are owned immediately and released on failure,
  cancellation, cell replacement/unload, budget shrink and Dispose. Destroyed or
  idle camera views expire; unused cloned materials also expire after five seconds.
- Inspector shows actual/reserved/peak bytes, buffer/group/view/material counts,
  cumulative freed payload, pending groups for the last camera and shared-frame
  upload bytes/CPU time. These are not total driver VRAM or GPU execution time.
  Counters reset with backend recreation. The memory audit button compares the
  live owned buffers with the ledger and checks allocated - freed = resident.
- Tools → Local Terrain → Check GPU Buffer Lifetime runs synthetic production-
  backend upload/lifecycle checks with actual GraphicsBuffers. Cameras are masked
  so it does not test images/FPS. Report: Logs/DetailProfiles/gpu-buffer-lifetime-check.txt.
  Pure CPU tests also cover shared quotas, time/byte/allocation exhaustion and
  next-frame progress. Real rapid-flight behavior still needs scene validation.
- Executed in isolated Unity 6000.3.23f1 / D3D12: eight partial/complete upload-
  unload cycles, two cameras, injected copy failure, budget shrink, oversized
  reservation rejection, destroyed camera and repeated Dispose all passed.
  Allocated/released buffer payload totals matched at 131,201,600 bytes; no live
  owned buffers remained. This is a synthetic resource-lifetime check, not a
  driver-wide leak test or a rapid-flight/FPS measurement.

## Expanded shader coverage and shared GPU selection (2026-09-24)

- Read-only `Tests/AuditDetailShaders.ps1` inventories saved layer prefab dependencies.
  The current inventory contains 36 unique prefabs: 17 grass, 13 stone and six
  driftwood prefabs, spanning three shader families. Variant dependency traversal
  can include overridden materials; this is NOT a live scene/profile measurement.
- Added indirect adapters for BaseShaderProps (GUID 8ce045feb4d898749ad119ec3bb00567)
  and package HDRP/Lit (GUID 6e4ae4064600d784cac1e41a9e6f2e59). Raw source adapters
  are explicitly allowlisted, retain the source's passes and leave its assets
  untouched. BaseShaderProps has no MotionVectors pass in its source; this change
  does not invent one. Static history is still explicit in the indirect hook.
- Retained build-variant subassets keep source keywords/render properties but
  replace modifiable textures with tiny placeholders of matching dimension:
  supporting HDRP/Lit must not pull unrelated project texture libraries into Resources.
  Null slots stay null; non-null slots must stay non-null because HDRP material
  validation derives _NORMALMAP/_MASKMAP from them. Runtime material clones
  still copy actual textures. Shader graph nonmodifiable texture defaults remain.
- Each camera/group shares one append/visible-ID buffer and compute dispatch for
  parts with the same `(LOD, prefab shadow mode, draw shadow mode)`. The selection
  kernel depends on root bounds and these modes, not individual mesh/material.
  Draw argument buffers, per-part transforms, materials and actual draws remain
  separate. Different cameras, LODs and near/far shadow partitions never share an
  output. A camera-masked part does not prevent a later compatible part dispatch.
- Memory reservation counts shared visible buffers once. Allocation limits,
  complete-group handoff, static previous transforms and fallback remain intact.
  Ten compatible parts therefore require one selection instead of ten; this is
  not a tenfold frame-rate claim. Root data still dominates many groups' memory.
- Inspector adds `GPU: общих проходов отсечения` and
  `Почему объекты остались на CPU? → Console`. The report aggregates prefab,
  shader, first fallback reason, group count and root candidates for the last
  camera pass. Upload waits are identified separately. Candidates are NOT visible
  counts, shader timings or the total loaded world; coarse-rejected groups are absent.
- Native tests now compile all three adapters and retained material keyword sets.
  Lifecycle tests also submit to disabled cameras and read indirect argument counts
  **only in validation**: ten parts / one dispatch / 65 instances per draw;
  two LODs and all shadow modes, including far ShadowsOnly rejection. Tests check
  shared memory ownership, partial uploads, two-camera budgets and failure cleanup.
- No density/LOD distance/material quality settings were changed. Player/car
  interaction remains out of scope. Actual lighting/motion/streaming in the scene,
  packaged-player shader stripping and GTX 1660 CPU/GPU frame times still require
  acceptance testing; no measured FPS improvement is asserted here.
- Verified on the host RTX 5070 with Unity 6000.3.23f1: three adapters' raster
  compilation and compute checks passed on D3D11/12; shared-selection argument
  and resource-lifetime checks passed on both APIs. Ten compatible parts in two
  cameras now own 23 buffers instead of 41 (same root and ten draws per camera).
  Final lifecycle fixture allocated/released 116,830,620 bytes and left no owned
  buffers. Reports: `Logs/DetailProfiles/gpu-expanded-shaders-d3d11-20260924.txt`,
  `gpu-expanded-shaders-d3d12-20260924.txt`, `gpu-shared-selection-d3d11-20260924.txt`
  and `gpu-shared-selection-d3d12-20260924.txt`. These are synthetic checks, not
  GTX 1660 performance or a packaged-player certification.

## Scale from density-mask patches

- Layer/Detail Stamp → Маска плотности детализации → Размер по маске.
  Opt-in, default off. Edge multiplier defaults to 0.35, inside to 1,
  scale softness to 0.35. Common/Own/None follow the existing mask inheritance;
  common settings affect all entries inheriting the mask, not just vegetation.
- Density and size reuse one procedural noise sample during generation. Density
  acceptance, seeds, candidate positions and random Scale Range are unchanged.
  Final uniform scale is random Scale Range multiplied by the mask factor.
- Size uses a smooth inward ramp from noise threshold `1 - coverage` to
  `threshold + scaleSoftness * coverage`; this is noise space, not geometric
  distance to an island centre or metres from the edge. Density softness and
  minimum density do not affect size. Zero/full coverage use edge/inside size;
  disabled masks return multiplier 1. Multipliers clamp to at least 0.01 to
  prevent singular transforms; reversed edge/inside values remain supported.
- The resulting scale enters the common CPU/GPU instance matrix, transformed
  bounds and LOD size before upload. No new frame-time noise evaluation or shader
  parameters. Larger plants may still change shading/overdraw and LOD choice.
- Preview has Density/Size tabs. Size brightness represents the multiplier relative
  to the displayed maximum, before random Scale Range; it is not an occupancy map.
  It uses the same evaluator as generation and does not modify authored settings.
- C# build and TerrainBridgeChecks passed, including 3600 deterministic samples,
  density invariance, inheritance, degenerate limits and matrix/bounds/LOD ordering.
  Live inspector interaction and appearance in the user's scene remain visual checks.

## Incremental detail size edits

- Painting, rock painting and displacement coverage use `LTSurfaceLayer.SurfaceHash()`.
  It covers surface texture references and surface/deformation properties, excluding
  detail entries, detail masks and the asset name. Texture-content hashes remain
  checked separately. Editing vegetation no longer invalidates these surface caches.
- Each generated cell has a full key and a placement key. The placement snapshot
  excludes only Scale Range and the four mask-size controls. All other entry fields,
  prefab recipe, effective density, seed, surface/chunk revisions and stamp area /
  suppression settings still invalidate placement. Future entry fields default to
  full invalidation. Stamp area keys do not serialize their vegetation settings.
- Size-only edits reuse accepted instance IDs, pivots, original rotations and local
  XZ sample positions. They rebuild size-dependent matrices, bounds, LOD sizes and
  CPU part caches within the existing generation budget. Sampling at the original
  terrain position avoids drift with normal-aligned height offsets. This stores
  24 additional payload bytes per CPU instance; GPU instance layout is unchanged.
- Unchanged groups retain their CPU caches and GPU resources. Changed groups are
  replaced cell-atomically, then uploaded through the existing shared GPU budget
  (CPU fallback until ready). The old cell remains visible if work is interrupted.
  This is not an in-place GPU scale update or a claim of zero upload cost.
- `Auto Refresh` discovers edits on its regular refresh pass. The manual rebuild
  button and assembly reload intentionally require full generation. Density, mask
  shape, coverage, seed, placement and prefab changes also require generation.
- `Tests/DetailRegenerationUnityCheck.cs` is an isolated native Unity fixture:
  copy it into the validation project's Assets, along with runtime LocalTerrain
  sources. Run `-batchmode -nographics -executeMethod
  LocalTerrainPrototype.DetailRegenerationUnityCheck.Run`. Do not run on an open
  user scene. A standalone HDRP validation project also needs the built-in
  Vehicles package when copying all runtime sources.
- Validation safety: never place a nested Unity project under the main project's
  `Temp`, and never junction/symlink its Packages to the main project's package
  cache. Cleanup of the nested project can traverse links and erase shared package
  contents. Use a separately owned directory outside Unity-managed folders and
  independent package copies/resolution. The old Temp-based validation setup was
  lost and must not be recreated. `RestoreUnityPackageCache.ps1` restores only
  empty package folders at their locked versions, with archive/file checksums.
- Passed on Unity 6000.3.23f1: all serialized surface/entry key fields; six fast vs
  full generation comparisons on translated sloped terrain; IDs, positions,
  matrices, culling bounds, LOD, part caches, unchanged group identity, empty groups,
  mid-cell cancellation and mask-coverage invalidation. Terrain samples were removed
  during each fast update to prove they are not read. C# build and CPU checks passed.
  This validates generation correctness, not live-scene latency or GPU performance.
