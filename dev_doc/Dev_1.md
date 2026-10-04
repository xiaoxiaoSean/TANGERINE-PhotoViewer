# TANGERINE PhotoViewer 功能开发文档（Dev_1）

本文记录当前代码的开发思路、工作原理和调用方式。主项目位于 `TANGERINE-PhotoViewer/`，桌面框架为 .NET 10 WPF。入口是 `App.xaml` 的 `StartupUri="MainWindow.xaml"`。文中方法名对应当前实现；这是实现说明，不把尚未实现的能力写成已完成。

## 1. 工程结构与调用关系

| 文件 | 职责 |
| --- | --- |
| `MainWindow.xaml`、`MainWindow.xaml.cs` | 主菜单、预览区、鼠标键盘操作、状态栏、异步任务调度、同目录切换 |
| `ImageLoader.cs` | 图片解码、超大图片局部读取、GIF 帧导出、内容识别 |
| `NativeWork.cs` | 在线程中运行原生图片处理代码，同时响应取消信号 |
| `GifWindow.cs` | GIF 导出窗口与进度 |
| `DetailsWindow.cs` | 图片详细信息窗口 |
| `AboutWindow.cs` | 只读且可复制的关于对话框 |
| `SystemWindow.cs`、`DefaultApps/` | Windows 文件关联注册、默认应用设置、取消设置 |
| `LanguageManager/` | 界面文字资源及语言回退 |
| `Assets/`、`IconAssets.cs` | 程序及窗口图标、启动 Logo、资源管理器图片文件图标 |

主要调用链：界面事件 → `MainWindow` 的异步方法 → `ImageLoader` 或 `DefaultApps` 服务 → 返回结果后在 WPF UI 线程更新控件。图片的扩展名只用于 Windows 文件关联列表；能否解码由图片内容及解码器决定。

## 2. 界面、图标和语言

**思路：** 主窗口和工具窗口采用黑色背景、白色文字；顶部菜单与底部状态栏固定，中间留给图片。启动时显示 Logo，加载图片后隐藏。

**原理：** `App.xaml` 设置通用控件样式；`MainWindow.xaml` 定义 `PreviewArea`、`Viewer`、`Navigator`、`NavigationOverlay`。主窗口图标由 XAML 中的 `Assets/TANGERINE-PV.png` 指定，子窗口由 `IconAssets.WindowIcon` 读取同一资源；项目文件还用 `.ico` 设置可执行文件图标。`RefreshMenu()` 根据当前是否有图片、是否为 GIF、是否有工作进行中，切换菜单和控件的可见性。`LanguageManager.Get(key)` 用当前 UI 区域性从 `UiStrings*.resx` 读取文字，缺项回退至 `en-US`，最终回退为键名。

**调用：** 启动时 `MainWindow()` 调用 `ApplyText()`；新增界面文字时，在各语言的 `UiStrings` 资源中加入同名键，再通过 `LanguageManager.Get("键名")` 赋值。不要在界面控件中直接写需要翻译的文字。`Window_Loaded` 还会读取命令行第一个文件参数并调用 `OpenAsync(file)`，用于系统文件关联打开。

## 3. 打开图片、格式识别和拖放

**思路：** 文件对话框、命令行、拖放和同目录切换共用 `OpenAsync(file)`，避免多套加载流程。新图片成功打开后才替换旧图片；失败或取消时用原状态恢复。

**原理：** 菜单 `Open_Click` 显示 `OpenFileDialog`；拖放通过 `Window_PreviewDragOver`、`Window_PreviewDrop` 检查单个存在的文件。`OpenAsync` 保存旧状态、启动取消令牌，把 `ImageLoader.Load` 放在后台执行。加载器先读取文件头判断 GIF 和少数需显式指定的后备格式，再尝试 `NetVips`；普通图片在 `NetVips` 失败时改用 `Magick.NET`。返回冻结的 WPF `BitmapSource` 后，主窗口重置旋转与缩放、更新尺寸、关闭旧图片相关窗口，并异步扫描同目录。成功时旧图才被替换。打开对话框的过滤器只影响选择列表，不是解码依据。

**调用：** 用户点“打开”、拖入单个文件，或执行 `TANGERINE-PhotoViewer.exe <图片路径>`。内部统一调用 `OpenAsync(file)`。`ImageLoader.Load(path, threshold, previewWidth, previewHeight, token, progress)` 返回位图、GIF 标记、尺寸、文件大小、格式、大文件标记及快速局部读取能力标记。

## 4. 大于 100 MiB 的图片与局部读取

**思路：** 以文件字节大小 `100 * 1024 * 1024` 为界；大文件先按当前图片浏览区的实际屏幕像素生成缩略图。停留在最小倍率时只显示缩略图。放大后按解码器的真实能力分流：可快速随机读取的分块格式直接取当前视口高清区域；其他格式在后台顺序解码一次并写成可随机读取的分块金字塔缓存，完成后读取高清区域。

**原理：** `OpenAsync` 用 `VisualTreeHelper.GetDpi(Viewer)` 把浏览区大小换算成屏幕像素，将宽高传给 `ImageLoader.Load`；大文件由 `NetVips.Thumbnail` 生成相应大小的缩略图，保留原始分辨率元数据。`ImageLoader.SupportsFastRegionAccess` 依据文件内容选中的 libvips 加载器读取 `ForeignFlags.PARTIAL` 能力；分块 TIFF 等真正支持局部读取的文件直接进入高清区域路径。若 TIFF 自带合适的金字塔缩小层，先选对应层，再按行拆为最多四段并行裁切，合成一张图更新画面。对条带 TIFF、常规 JPEG/PNG 等顺序格式，首次放大时 `PrepareCacheAsync` 在后台调用 `ImageLoader.PrepareRandomAccessCache`，使用顺序加载器把原图写成临时分块金字塔 BigTIFF；完成后同样通过最多四个线程读取可见区域。建缓存期间仍可缩放和拖动现有预览图，状态栏显示准备进度；取消、换图、卸载或关闭窗口会停止准备并清理临时文件。当前所需原图或可用金字塔层区域超过 1600 万像素时暂用预览图，控制内存。文件在处理期间被删除、失效或无法再读取时，异常会显示在状态栏。

**调用：** 用户正常打开大文件即可；最小倍率显示按浏览区分辨率制作的预览。可局部读取的格式在放大或移动时触发 `QueueRender()` → `RenderAsync()` → `ImageLoader.LoadRegionParallelAsync(...)`。其他格式首次放大时触发 `QueueRender()` → `PrepareCacheAsync()` → `ImageLoader.PrepareRandomAccessCache(...)`；缓存完成后自动调用并行区域渲染。创建缓存需要一次完整顺序扫描和足够的临时磁盘空间，10–20 GB 文件可能花较长时间，但不会重复启动多路全图解码。

## 5. 缩放、最小倍率和旋转

**思路：** 所有缩放入口共用 `SetScale`，并尽可能保持鼠标所在图片位置在缩放前后不变。打开图片后自动设置为当前视口的最小倍率。

**原理：** `MinimumScale()` 根据图片当前旋转后的宽高与 `Viewer` 可用宽高求适合视口的倍率，上限为 1；`Zoom()` 把倍率限制在该最小值和 `Math.Max(64, MinimumScale())` 之间。`SetScale()` 用 `(滚动偏移 + 锚点) × 新倍率 / 旧倍率 − 锚点` 算出新滚动偏移；鼠标滚轮缩放传入鼠标位置，其余入口默认以视口中心为锚点。`Rotate()` 以 90 度为单位更新旋转角，随后更新图片几何尺寸、鸟览图和大图局部渲染。

**调用：** 打开图片后菜单出现“放大”“缩小”“向左旋转 90 度”“向右旋转 90 度”。对应 `ZoomIn_Click`、`ZoomOut_Click`、`RotateLeft_Click`、`RotateRight_Click`；Ctrl+鼠标滚轮及 Ctrl+加/减键调用 `Zoom()`。当前按键处理识别 `OemPlus/Add` 与 `OemMinus/Subtract`。

## 6. 鼠标滚动、拖动、倍率显示和进度条

**思路：** 预览区负责直接浏览，底部进度条负责快速选择倍率；倍率提示只在数据变化时刷新。

**原理：** `Viewer_PreviewMouseWheel` 处理普通纵向滚轮、Shift+滚轮横向移动、Ctrl+滚轮以鼠标位置缩放；`WheelHook` 处理 Windows 横向滚轮信息。`Viewer_PreviewMouseDown/Move/Up` 捕获左键并按鼠标位移滚动图片。`ZoomSlider` 使用对数映射连接最小倍率与最大倍率，点击或拖动会调用 `SetScale()`；鼠标悬停时按轨道位置计算百分比并显示提示。按住 Ctrl 时 `UpdateZoomLabel()` 在左下角显示当前百分比，只有取整后的百分数改变才改写文字。

**调用：** 鼠标在图片上滚轮、按住左键拖动；按 Ctrl 加滚轮或加/减键缩放；点击/拖动底部滑块。相关事件均在 `MainWindow.xaml` 绑定到上述方法。

## 7. 鸟览图

**思路：** 高于最小倍率时显示整个图片的缩略导航图，并用框标出当前可见范围。它固定在预览区右下角，但允许调节大小。

**原理：** `UpdateZoomControls()` 按倍率决定 `Navigator` 是否可见。`SetDefaultNavigatorSize()` 默认取视口宽度的 1/8、高度的 1/7；`ClampNavigatorArea()` 用面积比例把它约束在预览区面积的 1/200 到 1/4，达到限制时由 `ShowNavigatorLimit()` 在左下角短暂提示。`UpdateNavigatorImage()` 使完整缩略图保持原图宽高比；`UpdateNavigatorViewport()` 按滚动偏移画可见范围。`NavigateTo()` 把鸟览图上的点击位置换算为主图滚动位置。左上角的 `Thumb` 用于改变鸟览图尺寸。

**调用：** 放大图片后出现鸟览图；点击或拖动鸟览图移动主图；在鸟览图上滚轮缩放主图，Ctrl+滚轮调整鸟览图大小；拖动其左上角控制点也可调整大小。

## 8. 同目录图片检测和方向切换

**思路：** 只有确认同目录还有其他可解码图片时，才显示左右切换按钮；扫描不阻塞打开图片。按钮随预览区尺寸变化，并尽量避开鸟览图。

**原理：** `ScanDirectoryAsync(currentFile)` 在后台枚举同目录文件，逐个调用 `ImageLoader.CanDecode`；优先用 `NetVips` 检查图片元数据，失败时用 `MagickImageInfo`，不根据后缀判断。扫描结果按当前文化排序。取消令牌和 `directoryScanGeneration` 防止旧扫描覆盖新图片的结果。`UpdateNavigationButtons()` 在存在其他图片时显示两个按钮：宽度=`PreviewArea.ActualWidth / 30`，高度=`PreviewArea.ActualHeight / 7`；左按钮的 x 为 0，右按钮的 x 为区域宽度减按钮宽度；两者默认顶部 y 为 `(区域高度 − 按钮高度) / 2`，因此矩形中心落在左右边中点连成的水平线上。若右按钮与鸟览图重叠，则把它移至鸟览图上方。`NavigateImageAsync(direction)` 用取模计算上一张或下一张，到目录边界后循环。

**调用：** 打开图片后等待目录扫描完成，点击预览区左/右侧按钮，或按左/上方向键切上一张、右/下方向键切下一张。预览区尺寸变化触发 `PreviewArea_SizeChanged()` 重新计算位置和尺寸；鸟览图尺寸变化时也会更新按钮位置。

## 9. 详细信息、GIF 工具和卸载

**详细信息：** 图片加载成功后 `DetailsItem` 显示在卸载项之前。点击调用 `Details_Click()`，把当前路径、宽高、文件大小和解码器报告的格式传给 `DetailsWindow`；窗口从语言资源组装各行文字。

**GIF 工具：** `ImageLoader.IsGif` 读取文件签名而非扩展名；确认为 GIF 时显示菜单。`Gif_Click()` 打开 `GifWindow`，可选择导出目录。`GifWindow.Export_Click()` 在后台调用 `ImageLoader.ExportGif()`，由 `MagickImageCollection` 合成帧后依次写为编号 PNG，报告百分比。工具窗口与主窗口通过 `WorkStateChanged`、`ProgressChanged` 同步工作状态和进度。

**卸载：** `Unload_Click()` 清除当前路径、图片源、尺寸、鸟览图、目录扫描结果，并关闭图片相关窗口；主界面重新显示启动 Logo。加载新图片成功时 `CloseImageWindows()` 也会关闭旧图片的附属窗口。

## 10. 停止工作、异步和进度

**思路：** 用户可以在主界面停止打开、卸载、目录扫描、局部读取和 GIF 导出等工作，避免后台结果在取消后覆盖当前界面。

**原理：** `BeginWork()` 为主图片操作创建 `CancellationTokenSource` 并递增 `generation`；`EndWork()` 清理当前操作。`Stop_Click()` 取消主操作、目录扫描和 GIF 导出，并停止待执行的延迟渲染。回调在写入 UI 前检查令牌、代次或当前文件。打开失败/取消由 `Restore()` 恢复旧图片。`Progress<int>` 把解码或导出百分比送到下方 `StatusLabel`。`NativeWork.Run()` 在后台 STA 线程执行图像任务，并以短周期检查取消信号，使等待方尽快返回。

**调用：** 操作进行中主菜单显示“停止工作”；点击调用 `Stop_Click()`。GIF 和系统设置窗口也有各自的停止按钮。需注意：取消会尽快停止等待和后续 UI 更新，但正在执行且无法被原生库中断的单次调用可能仍在后台运行一段时间；这不等同于强杀原生线程。目录扫描目前没有逐文件百分比显示。

## 11. Windows 默认应用设置与取消设置

**思路：** “系统操作”菜单常驻。窗口提供格式勾选、全选、全不选、设为当前默认、注册后打开 Windows 设置，以及取消本程序的全部关联。关联按当前用户登记，图片是否可打开仍由内容解码决定。

**原理：** `SystemWindow` 读取 `DefaultAppAssociationService.Formats` 创建复选框；`RegisterAsync()` 在后台依次处理选中扩展名。`RegisterHandler()` 先从程序集嵌入资源提取 `Assets/TPV_Photo.ico` 到当前用户的本地应用数据目录并验证哈希，再向 HKCU 写入 ProgID、打开命令、指向此独立 `.ico` 的 `DefaultIcon`、`OpenWithProgids`、应用能力和 `RegisteredApplications`，并用日志记录原值以便失败回滚。应用本身仍使用 `TANGERINE-PV.ico`，文件资源管理器中的图片文件则使用 `TPV_Photo.ico`。图标文件名包含内容哈希，更新资源时会使用新路径，避免沿用资源管理器的旧图标缓存。“使用本应用”再由 `SetThisAppDefault()` 按当前 Windows 用户选择格式，调用 `LatestUserChoice` 或 `SftaUserChoice` 写入并验证默认项；另一入口只注册候选程序后打开 Windows 默认应用设置页。`UnregisterAsync()` 调用 `DefaultAppUnregistrationService.Unregister()`，先处理属于本程序的显式默认选择，再删除本程序注册的格式与应用条目，并通知 Shell。服务用互斥锁避免并发修改。

**调用：** 主菜单“系统操作” → 勾选格式或用“全选/全不选” → 选择“使用本应用”或“打开默认应用设置”；“全部取消设置并删除注册”调用取消服务。具体可注册的扩展名以 `Formats` 列表为准；Windows 对默认应用选择的规则可能随系统版本变化，代码会验证结果并报告错误，而不会把写入注册表等同于设置成功。

## 12. 开发时的扩展入口

**关于对话框：** 顶部常驻“关于”菜单调用 `About_Click()`，以 `ShowDialog()` 打开 `AboutWindow`。窗口使用黑底白字的只读 `TextBox`，允许选中文字并复制；产品名称、致谢、创始人、版本标签和项目地址都由 `LanguageManager` 读取。版本统一在 `TANGERINE-PhotoViewer.csproj` 的 `<Version>` 中设置，窗口从生成程序集的版本号读取并显示为三段形式，当前为 `1.0.0`。

- 新增图片解码格式：先确认 `NetVips` 或 `Magick.NET` 能依据内容读取；必要时修改 `ImageLoader.Load/CanDecode`。若还要出现在“默认应用”勾选列表，再修改 `DefaultAppAssociationService.Formats`。两者用途不同。
- 新增主菜单功能：在 `MainWindow.xaml` 声明控件和事件；在 `MainWindow.xaml.cs` 添加处理方法，并在 `ApplyText()` 和 `RefreshMenu()` 中接入文字、显示条件。
- 新增可取消任务：使用令牌和代次检查；耗时工作移到后台；状态文字从 `LanguageManager` 获取；完成后只让仍然有效的任务更新 UI。
- 新增语言：在 `LanguageManager/UiStrings.<区域性>.resx` 添加对应键；中性资源和 `en-US` 是缺项回退来源。

## 13. 当前实现边界

- 对大于 100 MiB 且加载器没有 `ForeignFlags.PARTIAL` 能力的格式，首次放大需要顺序扫描原图并创建临时分块缓存，完成后才显示高清局部细节。TIFF 是否可以直接局部读取取决于内部是否分块，而不是 `.tif` / `.tiff` 扩展名。
- 目录扫描在图片打开后执行一次；目录后续变化不会被实时监视，重新打开图片会重新扫描。
- 停止原生解码时采用取消信号与结果丢弃，不保证瞬间终止正在运行的原生函数。
- 最小倍率当前采用完整图片适合预览区的计算方式；文档按代码行为描述。
