# Media Unseal Media Guide

This guide helps you use Media Unseal with a clear understanding of each choice, from selecting streams and output formats to checking the quality and compatibility of the result. It brings together practical instructions, explanations of containers and codecs, and an account of the processing technologies. Choose a topic from the contents or start with the overview.

## 1. Media Unseal Capabilities

### 1.1. Overview

Media Unseal gives you precise control over the streams in a media file. After analysis, you can select a specific video stream, audio track, subtitle track, or available image and save it as a separate file. The application displays codecs, stream properties, and metadata so that you can decide based on the file's contents, rather than its extension.

There are two basic modes. **Copying without re-encoding** preserves the original stream and suits work that requires the source material: a video track for editing, audio for further processing, or subtitles in their original format. **Conversion** creates a new stream with selected settings. It is useful when the output must meet the requirements of an editor, player, or platform. The availability of a particular encoder and output format combination depends on the installed FFmpeg and is checked by the application.

You can process several streams from one source file in a single run. For example, save two dubbed audio tracks and a subtitle track, placing the outputs in language folders. Each selected stream gets its own output file; the source remains unchanged. A log records the process, which you can cancel.

This guide answers two kinds of questions. **Using the Application** explains how to set up and run a task. The chapters on containers, codecs, and processing technologies explain why a method works, what compatibility depends on, and which settings actually affect the result. They help you choose how to create the file you need.

### 1.2. Extracting Video Without Audio

Extraction saves a video stream as a separate file without sound and without re-encoding. Media Unseal copies the original video data into a suitable output container: the codec, resolution, frame rate, and image quality remain unchanged. The source file is not modified.

Add a file and wait for analysis to finish. In the stream table, tick the checkbox **in the first column on the left** of the video track you need. You can also select a stream by double-clicking its row; double-click again to deselect it. Make sure no audio tracks are selected.

In the settings panel, leave **Video format** at its default, **Original — no transcoding**. Choose an output folder or keep the default, then click **Extract** at the bottom of the window. If you select several video tracks, the application saves each one to a separate file, all without re-encoding or adding sound.

The output container is chosen according to the stream's codec and may differ from the source container. For example, H.264 may be saved in MKV even when the source was MP4. The video stream itself remains in its original form.

### 1.3. Extracting Audio Tracks

Extraction saves a selected audio track as a separate file without re-encoding. The original audio stream is copied with its codec, sample rate, and channel count; video and other unselected streams are not included. The source media file is unchanged.

Add a file and wait for analysis. In the stream table, tick the checkbox **in the first column on the left** of the audio track you need. You can also double-click its row; double-click again to deselect it. If you need audio only, make sure video and subtitles are not selected.

In the settings panel, leave **Audio format** at **Original — no transcoding**. Choose an output folder or keep the default, then click **Extract** at the bottom. You can select several audio tracks, such as different language versions: each is saved to its own file without re-encoding. To sort the results into language subfolders, enable **Group by language (create subfolders)**.

The output container depends on the track's codec and may differ from the source container. For example, AAC is saved as a separate `.aac` file, Opus as `.opus`, and ALAC as `.m4a`. The codec and audio data remain original.

### 1.4. Extracting Subtitles

Extraction saves a subtitle track as a separate file without changing its contents. This lets you obtain the original lines, formatting, or subtitle images for further work. The source media file is unchanged.

Add a file and wait for analysis. In the stream table, tick the checkbox **in the first column on the left** of the subtitle track you need. You can also double-click its row; double-click again to deselect it. If you only need subtitles, leave video and audio unselected.

In the settings panel, leave **Subtitle format** at **Original — no transcoding**. Choose an output folder or keep the default, then click **Extract** at the bottom. If you select several tracks, each is saved separately. For tracks in different languages, you can enable **Group by language (create subfolders)**.

The source track determines the output file type: SubRip becomes `.srt`, ASS becomes `.ass`, WebVTT becomes `.vtt`, and image-based PGS becomes `.sup`. PGS contains images, so extraction does not turn it into editable text; the application does not perform character recognition.

### 1.5. Converting Video

Conversion is useful when video must be saved in another output format or prepared for a particular editor, player, or platform. Media Unseal checks whether the source video stream is compatible with the selected container. If it is, the stream may be moved without changes; otherwise the application creates a new stream with a suitable encoder. Re-encoding usually takes longer, but makes it possible to obtain the required combination of container and codec.

Add a file and wait for analysis. Tick the video track **in the first column on the left**, or double-click its row. In the settings panel, open **Video format** and choose the desired option, such as AVI or MP4. Choose an output folder or keep the default. Once the selected format has been checked, click the action button at the bottom: **Process** if re-encoding is required, or **Extract** if all selected streams can be moved without it.

If you select several video streams from one file, each is saved separately in the chosen output format. The application checks each stream: some may only need a new container, while others require re-encoding. For example, when you select AVI for streams with different codecs, the method used to create each output may differ. The source file remains unchanged.

**Configure** lets you choose an encoder and processing settings. The settings selected there apply to every selected video stream. If different tracks need **different encoders**, process them one at a time, selecting the appropriate track and settings for each run. The **Video Settings** article explains these controls in detail.

### 1.6. Converting Audio

Audio conversion prepares a track for a player, editor, or other use: for example, saving it as MP3, FLAC, or WAV. Media Unseal checks the selected output format and chooses a suitable encoder from the installed FFmpeg. If the source stream is compatible with the new container, the application may move it without re-encoding; otherwise it creates a new audio stream. Re-encoding usually takes longer and can change the sound, especially when converting between lossy formats.

Add a file and wait for analysis. Tick the audio track **in the first column on the left**, or double-click its row. In the settings panel, open **Audio format** and choose the desired option. Choose an output folder or keep the default, then click the button at the bottom: **Process** for re-encoding or **Extract** if the stream can be saved without it.

If several audio tracks from one file are selected, each gets its own output in the chosen format. Enable **Group by language (create subfolders)** if needed. The application checks each track separately, so the method of processing can differ even when the output format is the same.

Use **Configure** to choose an encoder and audio settings for the selected tracks. If each track needs different settings, process them one at a time. Bitrate, sample rate, and channels are explained in **Audio Settings**.

### 1.7. Converting Subtitles

Subtitles can be saved in their original form or converted to another text format. Media Unseal handles each selected track separately: for example, SubRip can be saved as SRT, while compatible text subtitles can be converted to ASS or WebVTT. Formats differ in their support for styling, so check the timing and appearance of the output after conversion.

Add a file and wait for analysis. Tick the subtitle track **in the first column on the left**, or double-click its row. For simple extraction, leave **Subtitle format** at the original option and click **Extract**. To convert, choose the required text format, select an output folder or keep the default, and start the task with the button at the bottom. It reads **Process** if conversion is required, or **Extract** if the stream can be saved without it.

You can select several tracks from one file; each is saved separately. If the subtitles use different languages, enable **Group by language (create subfolders)** if desired. The selected output format applies to all selected text tracks; to produce different formats, process the tracks in separate runs.

Image-based subtitles such as PGS can be extracted in their original form as `.sup`. The application does not recognize text in images, so it cannot turn PGS into SRT or another text format. The **Subtitle Settings** article explains the difference between text and image-based tracks.

### 1.8. Images and Cover Art

Analysis distinguishes video streams, still images, and cover art. Confirmed JPEG and PNG images can be extracted from a suitable stream or MKV attachment. Preview first tries to show cover art, then obtains a video frame through FFmpeg.

Recognizing WebP, BMP, TIFF, and other graphics formats does not imply a separate export route or an image editor. Image previews have a size limit; a damaged or ambiguous attachment may fail to open.

### 1.9. GIF and Other Animated Formats

Creating a short GIF from video through the current interface was **confirmed manually by the user**. Select a video stream, open **Configure** next to **Video format**, find GIF under **Output container / format**, click **Apply**, and start conversion. This is a practical confirmed case, not a promise about every duration, palette quality, or input file.

The application can recognize GIF, APNG, and animated WebP during analysis. A dedicated animation wizard, palette controls, and export of existing animated input streams have not been confirmed. APNG, WebP, and AVIF in the FFmpeg catalog remain candidates or capabilities of the FFmpeg build itself.

### 1.10. MKV Attachments

An MKV container can hold embedded files as well as media streams: cover art, subtitle fonts, and other material. Media Unseal finds them during analysis and shows a separate **Attachments** row in the table. The application reads attachments with its own Matroska module and saves their original data without re-encoding.

To select files individually, right-click the **Attachments** row and open **View**. The **MKV Attachments** window shows file names, types, and sizes. Tick the boxes to the left of the attachments you want, then close the window. Your selection remains part of the main task: choose an output folder and click **Extract** at the bottom of the main window. You can process attachments alongside selected media tracks this way.

If you need **all** attachments, use the selection button on the left of their row. They will be included in the main run. There is also a direct command: right-click the **Attachments** row and select **Extract all**. This immediately starts extraction of every MKV attachment, regardless of which other tracks are selected.

Attachments are saved as separate files. The application checks their positions inside the MKV and does not overwrite existing outputs.

### 1.11. Processing Multiple Tracks

Media Unseal can produce several outputs in one run. You can tick individual table rows or click **Select or deselect all streams** in the first column header. It selects all available streams in the current file, including MKV attachments if they were found.

Review the selection before starting. The settings panel has separate format fields for video, audio, subtitles, images, and animations: for example, you can send video to one format and audio to another. If several streams of **one type** are selected, that type's format applies to all of them. To give two audio tracks different output formats or settings, process them separately.

Choose an output folder or keep the default. If needed, enable a folder named after the source file and language subfolders for audio and subtitles. Then click the action button at the bottom. It reads **Extract** for copying without re-encoding, or **Process** if at least one stream will be converted.

Every stream and selected attachment is saved to a separate file. You can follow progress and the log, or stop the task with **Cancel**. A combined run applies to streams from **one source file**.

## 2. Using the Application

### 2.1. First Launch and FFmpeg

On startup, Media Unseal checks two external tools: **FFprobe** analyzes a file and reports its streams, while **FFmpeg** performs most extraction, conversion, and preview operations. The application also has its own Matroska module for reading and copying MKV attachments. Media processing remains unavailable until both FFmpeg and FFprobe pass their checks.

If both tools are found and marked ready, no further setup is needed: you can add a file immediately. If either tool is missing, open **Settings → FFmpeg installer**. It shows the status of FFmpeg and FFprobe separately. Use an available installation method, or click **Locate FFmpeg** and **Locate FFprobe** if the tools are already installed. For manual selection, point to the executable files themselves, not the folder that contains them.

After installing or changing paths, click **Check again**. Once **All components are ready** appears, the application can analyze and process files. Selected paths are saved; if you later move or remove the tools, you will need to locate them again.

The available output formats and encoders are determined by the installed FFmpeg build. Their list may therefore differ on another computer even when the Media Unseal version is the same.

### 2.2. Adding and Analyzing a File

Media Unseal can open a local video or audio file, a standalone subtitle file, an image, or an animation. It does not reject files based on a short extension list: it checks their actual contents. An unusual extension alone does not prevent analysis, but opening still depends on the installed FFprobe and the condition of the file.

To add a source, click **Choose file** and select it in the dialog, or drag a file into the application window. You can select **one** local file at a time; dropping several files together does not add them. Analysis starts automatically after selection, without a separate Start button.

FFprobe identifies the container and lists the streams it finds: types, codecs, duration and available properties, names, and languages. Media Unseal uses this information to populate the table and distinguish ordinary video, images, and animations. For an MKV with signs of attachments, it also checks them with its own Matroska module. Analysis only examines the source; extraction or conversion starts after you select streams and launch processing.

Analysis time varies with the structure and condition of the file, the number of streams and attachments, and the speed of access to the source, whether on a local disk or network storage. File size alone is not a reliable predictor. Selecting a different source cancels unfinished analysis of the previous one. If a file cannot be opened, check the application message and **Event log**: possible causes include damage or contents that the current FFprobe cannot handle.

### 2.3. The Media Streams Table

The table fills after file analysis. Each row represents a detected stream: video, audio, subtitles, image, or animation. Only types actually present in the source appear. If embedded files are found in an MKV, a separate **Attachments** row is added. The application does not present an unknown visual stream as ordinary video; its type and availability are determined separately.

| Column | What it shows |
|---|---|
| Selection on the left | A checkbox adds the stream to the task. The header button selects or deselects all available streams. An unavailable stream cannot be selected. |
| Type | Video, audio, subtitles, image, animation, or another recognized type. MKV may have a separate Attachments row. |
| Index | The stream number within the source file. The shared Attachments row has no stream index. |
| Language | The language from stream metadata. If absent, a dash is shown; the application does not identify a language from speech or text. |
| Codec | The stream format and its profile if known. The file container and stream codec are different things. |
| Bitrate | The stream data rate if FFprobe reports one. A dash means the information is absent, not that the bitrate is zero. |
| Channels / Resolution | Channel count for audio; frame dimensions for video and images; text or image type for subtitles. Missing information is shown as a dash. |
| Status | The current processing option or the state of an operation already started. |

To select a row, tick its checkbox on the left or double-click the row; repeat the action to deselect it. Right-clicking an ordinary stream opens **Properties**, with more detailed parameters and metadata. That window is for inspection, not editing. On the **Attachments** row, right-click offers **View** to select individual files and **Extract all**. See **MKV Attachments** for details.

**Read Status in the context of the selected format.** Before a run, it shows what the application can do with a stream; afterwards, it shows progress and the outcome:

| Status | Meaning and next step |
|---|---|
| **Available** | The stream has an extraction route without re-encoding and can be selected. |
| **Conversion available** | There is no ready route for original extraction, but the stream can be converted after you choose a suitable format and settings. |
| **Needs configuration** | The stream is selected, but there is no valid processing plan yet. Check the format, encoder, or setup error. |
| **Conversion** | Conversion has been configured for the selected stream but has not started yet. |
| **Extracting / Converting** | Processing is under way. |
| **Extracted / Converted** | Processing of that stream succeeded. |
| **Error / Cancelled** | Processing did not finish successfully; see the row message and event log. |
| **Unavailable** | There is no available extraction or conversion route. Its checkbox is disabled. |

Hover over the **Status** cell for a more detailed message, particularly when a stream is unavailable or its settings are invalid. The shared Attachments row shows the number of files found instead of an ordinary status. **Available** indicates a processing route, but success with a specific file also depends on its condition.

### 2.4. Folders and File Names

**Where outputs go.** In the export settings, check **Output folder**. On first launch the application suggests the system Downloads folder if it is available; a folder you choose is remembered for later runs. Click **Browse** to select another folder, or type a path and press Enter. The typed path is also accepted when you move focus to another control. The folder must already exist and be writable. If validation fails, the application shows an error and restores the previous path. Processing cannot start while typed text remains unconfirmed.

**How files are organized.** With no extra options, all outputs go directly into the selected folder. **Create a folder named after the source file** places them in a new or existing subfolder using the source name without its extension. **Group by language (create subfolders)** places audio and subtitles in folders named for the language in their metadata; a stream without a language goes into a folder labeled “language not specified.” Video, images, and animations stay in the common folder. If both options are enabled, language folders are created *inside* the source folder.

**How names are built.** For an ordinary stream, the application combines the source name without its extension, the stream type and index, and any available language and title metadata. The extension matches the chosen output format. For example, a Russian audio stream follows a pattern like source_audio_index_ru_title.extension; missing parts are omitted. MKV attachments retain their own names. Characters invalid in file names are replaced, and overly long names are shortened.

**If the name already exists.** An existing file is not overwritten: the new name receives a number in parentheses, such as source_audio_1 (2).mka. This also applies when you process the same source again. When finished, click **Open folder** at the bottom of the window to open the selected main folder, then navigate into any subfolders created. Before a lengthy conversion, check the path and free disk space.

### 2.5. Video Settings

Select a video stream using the checkbox on the left of the table. In export settings, you can choose **Video format** directly; **Configure** opens **Advanced conversion settings** for finer control. First choose **Output container / format**, which determines the output file type, then **Encoder**, which determines how the video stream is compressed. These are different settings: a container may accept several codecs, but not every combination. The list depends on the installed FFmpeg. **Find more encoders** checks additional options for the selected format, while **Hardware encoders** checks options available on this computer. Seeing an encoder in the list does not yet guarantee successful encoding of your particular source.

The fields below are **optional**. In each, you can select a suggested value or type your own. An empty field with the hint **Automatic** does not set that parameter manually. Enter only the changes you need:

| Setting | What it changes and what to consider |
|---|---|
| **Quality / CRF** | Controls compression quality if the selected encoder supports CRF. With many common software encoders, a lower value means fewer losses and usually a larger file. It is neither a quality percentage nor a universal scale across encoders. A disabled field means the selected encoder has not confirmed CRF support. |
| **Bitrate, kbps** | Sets the target amount of video data per second. A higher bitrate often increases file size and can preserve detail better, but cannot repair flaws in the source. Output size and quality also depend on duration, scene complexity, and encoder behavior; the number does not guarantee an exact file size. Usually start with one quality control method, CRF or bitrate, and assess the result. |
| **Width and height, px** | Change frame dimensions during re-encoding. If only one field is filled, the application asks FFmpeg to calculate the other while preserving proportions; the exact size may be rounded to meet encoder requirements. Downsizing removes some detail; upsizing does not restore detail that was never present. Setting both values to a different aspect ratio may stretch or squeeze the image. |
| **Frame rate, fps** | Sets the *output* frame rate. When reducing it, FFmpeg drops excess frames; when increasing it, it may repeat neighboring frames. Neither operation creates new moments of motion. Converting 60 to 30 fps can therefore look less smooth, while converting 25 to 60 does not turn the source into footage shot at 60 fps. Leave this field empty to preserve normal motion. |

If **Original** is selected for the source video stream and no parameters are set, an available copy route preserves it without re-encoding. Changing quality, dimensions, or frame rate requires an encoder and re-encoding. **Force encoding** appears when the application could otherwise copy a stream using the same codec; enable it if you specifically need a new encoding pass. After making your selections, click **Apply**. The application checks the combination of format, encoder, and parameters. The settings apply to every selected video stream; process streams separately if they need different settings.

### 2.6. Audio Settings

Select the audio track you need, choose **Audio format**, and click **Configure**. The window lets you set **Output container / format** and **Encoder**. The format determines the file type; the encoder determines the audio representation inside it. The application suggests a suitable option, and **Find more encoders** checks additional ones. Audio fields accept a value from the list or a number you type; empty fields leave the choice to FFmpeg.

| Setting | What it changes and what to consider |
|---|---|
| **Bitrate, kbps** | Sets how much data is allotted to each second of compressed audio. Other things equal, a higher bitrate usually needs more space and may reduce losses, but re-encoding a compressed recording does not restore lost detail. The right value depends on the encoder, channel count, and target device. |
| **Quality, 0–10** | Passes a separate quality setting to the encoder. It is **not a rating of the source track out of ten**: the meaning, and even the direction of its effect, depend on the selected encoder. Some encoders may not support it. Leave the field empty unless you have a reason to set it; otherwise check the result by listening and reviewing the compatibility message. |
| **Sample rate, Hz** | Sets the output sample rate, the number of signal samples per second. FFmpeg resamples when it changes. Reducing the rate can remove the upper part of the frequency range; increasing it does not create details absent from the source. Change it chiefly to meet format or device requirements. |
| **Channels** | Sets the number of output channels: for example, 1 for mono, 2 for stereo, or 6 for multichannel audio. Reducing the count mixes channels and loses their original spatial separation; increasing it does not create independent new recordings. Consider the playback device's capabilities. |

To preserve the original encoding and channel count, leave **Original — no transcoding** selected and do not set conversion parameters. The window may offer **copy**: this copies audio into the selected container, which the application checks separately for compatibility. When encoding with the same codec as the source, **Force encoding** lets you request a new encoding pass. Click **Apply**; the check may reject an unsupported combination. Audio settings apply to all selected audio tracks, so process different language tracks separately when they need different settings.

### 2.7. Subtitle Settings

Select a text subtitle stream and click **Configure** next to **Subtitle format**. The window has two main fields: **Output container / format** determines the file type, and **Encoder** determines how subtitles are written into it. Unlike video and audio, there are no bitrate, quality, frame size, or frame rate fields: the application does not adjust appearance and timing with separate numeric controls. **Find more encoders** can check additional options for the selected format; the search is unavailable for the original subtitle format. If **Force encoding** appears, it requests that text be written anew instead of using an available copy route. Click **Apply**; the application checks the combination against the installed FFmpeg.

Choose according to how the file will be used. **SRT** is convenient for ordinary text and widely supported, **ASS** retains more styling options, and **WebVTT** is designed for the web. Converting to a simpler format may lose styles, positioning, and other features; inspect the result in the player where it will be used. To keep the original form, leave **Original — no transcoding** selected.

Image-based subtitles such as PGS contain pictures of the captions. The text conversion window does not recognize characters in those pictures and cannot turn them into SRT or ASS. Use an available original extraction route for such a stream, for example PGS to SUP. If several text streams are selected, the chosen format is applied to each separate output file; bear in mind that their styling may differ.

### 2.8. Processing, Cancellation, and the Log

**Starting and tracking a task.** After selecting streams, output formats, and a folder, click the main button at the bottom. It reads **Extract** when the selected streams can be saved without re-encoding, or **Process** when at least one must be converted. Here, **Process** means starting conversion; any streams in the same selection that only need extraction are not re-encoded because of the button label. Ordinary selected streams become separate files; an MKV Attachments row can yield several files. During the run, the status line names the stream being extracted or converted, and its table row status changes.

The bar at the bottom shows overall task progress; a percentage for the current stream and elapsed time may appear nearby. If the source has no reliable duration or FFmpeg has not yet reported its position, an exact percentage may temporarily be unavailable. That alone is not an error. With several streams, the application shows their progress in order; completing one file does not mean the entire task is complete. At the end, check the summary and row statuses: the queue may finish fully, have errors on individual streams, or stop after cancellation. **Open folder** takes you to the selected main output folder.

**Cancellation.** Click **Cancel** to stop current processing and the remaining queue. Files already saved completely stay in the output folder; an incomplete output is not presented as finished. The application removes temporary files and its temporary processing folder. If a permission error or changed path prevents cleanup, this is recorded in diagnostics; inspect the folder manually if necessary. Stopped and not-yet-started streams receive the appropriate status, and you can select and run them again.

The **Event log** in the main window is a short history of messages written for users: FFmpeg and FFprobe checks, analysis starting and finishing, MKV attachments found, extraction or conversion starting, individual streams completing, cancellation, and errors. Each row has a time. Color helps you locate the outcome:

| Highlight | What to look for |
|---|---|
| Green | A successfully completed action, such as a saved stream. |
| Yellow | A warning or cancellation; read the message to see whether action is needed. |
| Red | An analysis, configuration, or processing error; start investigating here. |
| No color emphasis | A routine progress message that does not yet indicate success or failure. |

Use the log for a quick check of which stream finished, which failed, and when a warning arose. The latest **200** user-facing messages remain on screen. **Clear** above the log removes them from this list only; it does not erase the saved diagnostic history.

**Diagnostic Report.** Click **Save log** above the event log to open the **Diagnostic report** window first. There you can inspect more detailed records, grouped by date and application run: time, operation category in English, **INFO / WARN / ERROR** level, and a technical message. Levels are color-coded, with counts of events, warnings, and errors at the bottom. Use it to investigate causes that do not fit in the short log, including compatibility checks and FFmpeg failures.

Click **Save** in the report window to write it to a text file. If you report a bug or observation to the developer, describe the source, selected settings, and observed result; attach this file if useful, after reviewing its contents. The application does not send the report automatically. **Clear** *in the report window* deletes the saved diagnostic history. Storage has time and size limits, so save a report soon after an issue if you may need it later.

### 2.9. Application Settings

Open **Settings** with the gear button at the top of the main window. The sections on the left are **FFmpeg installer**, **Appearance**, **Interface language**, **Knowledge base**, and **About**. Select one to see its controls on the right. Changes to appearance, language, and scale take effect when selected and are saved for future launches; there is no separate Save settings button. These options change the interface, not the encoding of files or previously saved outputs.

**Appearance.** Choose one of five theme cards. Color samples on the right let you compare the background, panels, accent, and text before selecting. The themes differ in density and the visual character of the workspace:

| Theme | Appearance |
|---|---|
| **Studio Dark** | A high-contrast studio style; the default theme. |
| **Resolve Workspace** | A denser workspace. |
| **Adobe Clean Pro** | Restrained typography and more generous spacing. |
| **Industrial Utility** | A strict, utilitarian look. |
| **Hybrid Modern Panel** | Soft panels with a cool color accent. |

The theme changes immediately, so you can try several and keep the one you prefer. The highlighted card shows the current selection. Themes affect colors and presentation, not available formats, FFmpeg behavior, or export settings.

**Interface scale.** Below the theme cards is the **Interface scale** list. **Automatic** chooses element sizes with the display in mind. If text and buttons look too small or large, choose a fixed scale from **75% to 125%** in 5% increments. This changes application controls, not video resolution or the size of a future output file. The choice takes effect immediately and is remembered. On a small display, available space may also constrain the window, so 125% does not always mean the window can grow by exactly that amount.

**Interface language.** The **Interface language** section shows language cards. Click an available card or its switch to apply that language to labels and messages without another confirmation; the choice is saved. The active card is highlighted. Dimmed cards with disabled switches cannot be selected. A language appearing in the list does not by itself mean it is available in the installed build. This setting does not determine the language of source audio or subtitles.

**FFmpeg installer and Knowledge base.** **FFmpeg installer** shows the status of required components and lets you check them or configure their paths again. **First Launch and FFmpeg** explains the initial setup; correctly working tools need no repeated configuration. In **Knowledge base**, click **Open media guide** to open the media guide.

**About and updates.** This section shows the application version and information about the developer and components. **Check for update** performs a real check and displays a status: checking, already up to date, a newer version found, or an error. When an update is available, **Download and update** appears. Whether it can install automatically depends on the installation method and platform; the application may instead offer a manual route. You cannot start an update while a file is being processed; wait for the task to finish. Checking for a new version alone installs nothing.

The section also has a **Support the developer** link, which opens a separate window with available ways to support the project voluntarily.

### 2.10. Common Errors

If an operation fails, first read the affected row's **Status** and hover over it for a more precise explanation. Then check any red or yellow entry in the **Event log**. For details, open **Save log → Diagnostic report**. One stream failing does not necessarily mean the others were lost: inspect every row's outcome and the output folder.

**A file will not open or processing will not start**

| What you see | What to check |
|---|---|
| **FFmpeg or FFprobe is missing; the application is not ready** | Open **Settings → FFmpeg installer**, check both tools and their paths, then click **Check again**. FFprobe is needed for analysis and FFmpeg for most processing operations. |
| **File not found** or **Access to file denied** | Make sure the source has not been moved or deleted, your account can access it, and its drive is connected. Select it again. |
| **Could not start FFprobe** | Repeat the component check in the installer. A path existing does not prove that the executable starts. |
| **The file may be damaged or unsupported** | Try opening it in another player and check whether its copy or download completed. FFprobe may be unable to read a damaged file or contents unsupported by the installed build. The extension alone does not prove the format. |
| **FFprobe timed out** | Try analysis again. For a source on a network or slow drive, try a local copy. If only this file repeatedly fails, save a Diagnostic Report. |
| **Analysis cancelled** after selecting another file | Changing sources stops unfinished analysis of the previous one. Let analysis of the new source finish. This message alone does not mean the previous file was damaged. |
| **No thumbnail, but streams were found** | Preview failure does not equal analysis or extraction failure. Use the stream type and status in the table; preview error details are in the log and report. |
| **Extract or Process is disabled** | Wait for analysis and format checks, tick an available stream on the left, check the output folder, and make sure no selected stream has **Needs configuration** status. Starting is blocked while any selected stream lacks a valid processing plan. |

**The stream is visible, but the format does not fit**

| What you see | What to check |
|---|---|
| **Unavailable**; the stream cannot be ticked | The application found no available extraction or conversion route. Hover over Status. Renaming the source extension will not add support for its contents. |
| **Conversion available** or **Needs configuration** | No ready original-copy route exists, or the chosen conversion is not yet configured. Choose an output format, open **Configure**, and check the encoder and the row's specific message. |
| **The source codec cannot be copied into the selected container** | The container and codec are incompatible for copying. Choose another output container or an encoder for re-encoding. Renaming the file will not help. |
| **No suitable encoder**, or the check did not produce a test file | Try another listed format or encoder. **Find more encoders** tests extra options, but the outcome depends on the installed FFmpeg. Do not assume every FFmpeg encoder works with every container. |
| **Enter numeric values or leave fields empty** | In **Configure**, correct a field containing letters, an invalid separator, or an excessively large number. An empty field means Automatic; a value must be within the allowed range. |
| **Video cannot be copied to the selected format** | Possible causes include the codec, profile, frame rate, resolution, color format, or source bitstream structure. Read the specific compatibility message: another container may suffice, or re-encoding may be necessary. For timestamp or header errors, open the Diagnostic Report. |
| **Image-based subtitles cannot become SRT or ASS** | PGS and similar subtitles contain images of text. There is no OCR; use an available image-based extraction route, such as PGS to SUP. |
| **Hardware encoder search found nothing or failed** | The check applies to the selected format and current system. Use an available software encoder, or check hardware acceleration and FFmpeg. Finding none does not block every conversion. |

**Saving, output, and logs**

| What you see | What to check |
|---|---|
| **Output folder not found** or **Invalid path** | Select an existing folder through **Browse**, or correct the path and press Enter. If the folder was deleted after selection, choose it again. |
| **Access to folder denied** or **Could not write file** | Choose a writable folder, check free space and drive availability, and check the connection for external or network storage. Completed outputs from other streams may remain. |
| **Could not start FFmpeg** | Return to **FFmpeg installer** and check again. If the path changed after application launch, locate the current executable. |
| **Processing timed out** | See which stream and format Status names. Try again with a local source and accessible output folder. If the failure recurs, save a Diagnostic Report. A timeout alone does not identify one specific cause. |
| **Could not read or extract an MKV attachment** | Check that the source MKV opens and that the attachment appears under **View**. Other streams may remain available if attachment listing fails; see the Diagnostic Report for technical details. |
| **Queue completed with errors** | This is an overall result, not a diagnosis of every file. Compare row statuses with the log: successful outputs remain saved; you can reconfigure and rerun the failed ones. |
| **Some files remain after Cancel** | This is expected: fully completed files remain while incomplete temporary outputs are removed. **Not started because of cancellation** applies to streams the queue never reached. |
| **Output is not where expected** | Click **Open folder** and check the source-folder and language-grouping options. When names collide, the application adds a number in parentheses rather than replacing an earlier file. |
| **Extracted video has no sound** | A video stream is saved separately from audio tracks. Select an audio track for separate extraction if needed; silence in the video-only output does not mean it is damaged. |
| **Could not check for updates** | This concerns version checking, not media processing. Try later and check network access; the Diagnostic Report may contain details. |

If these checks do not help, save a **Diagnostic Report** immediately after reproducing the issue. State which file and stream you processed, the output format and encoder you selected, what you expected, and what happened. Review the report before sharing it.

## 3. Containers and File Formats

### 3.1. What Is a Container?

A container defines **how streams and information about them are organized in a file**: where video, audio, and subtitles reside; how their timestamps relate; and where languages, titles, chapters, and other metadata are stored. A codec solves a different problem: how the frames and audio samples themselves are represented and possibly compressed. Thus, “video in MP4” does not specify a compression method; it may contain different video codecs. Conversely, H.264 can appear in different containers.

{{diagram:container}}

A file contains track descriptions and data blocks, often with an index for seeking to a particular time. Timestamps synchronize picture and sound. If they are damaged, playback may fail even when the codec is supported. A container may permit multiple languages, subtitles, chapters, cover art, or attachments, but **the particular format and application** determine which capabilities are actually available.

| Layer | What it defines |
|---|---|
| **Container** | File composition, stream layout, timestamps, and metadata. |
| **Codec** | Representation of video, audio, or subtitles within a stream. |
| **Extension** | A suggested file format; on its own it does not prove the contents. |
| **Encoding settings** | Bitrate, resolution, quality, and other properties of a particular stream. |

Formats arose for different purposes. AVI and WAV are based on RIFF. QuickTime and the MP4 family that grew from it organize data in a hierarchy of blocks. Matroska uses the extensible EBML structure. WebM builds on Matroska but restricts codecs for web delivery. These origins help explain their familiar uses: archives with multiple tracks, exchange between devices, editing, or online publishing. [QuickTime](https://developer.apple.com/documentation/quicktime-file-format/atoms), [MP4](https://www.iso.org/standard/79110.html), [Matroska](https://www.matroska.org/technical/basics.html), [WebM](https://www.webmproject.org/about/).

**A container is not itself “lossy” or “uncompressed.”** A large MP4 and a small one may differ in codec, bitrate, duration, or resolution. WAV often contains uncompressed PCM but can hold other audio. Copying a stream into another permitted container changes its packaging without re-encoding. Conversion creates a new stream and may change quality. Renaming the extension does neither.

After analysis in Media Unseal, look at the **Type**, **Codec**, and **Status** columns. To preserve the original encoding, leave **Original — no transcoding** selected. If you need another container, the application checks whether copying is allowed; if it is not, configure an encoder. Successful file creation does not guarantee playback on a particular television, phone, or editor. Test the result where you intend to use it.

### 3.2. MP4 and MOV

**A shared historical root.** MOV is associated with QuickTime, where tracks and supporting information are stored in a hierarchy of blocks called *atoms*. MP4 grew from the ISO Base Media File Format family. Both containers can carry video, audio, and metadata; the extension does not tell you which codec created a stream. [QuickTime structure](https://developer.apple.com/documentation/quicktime-file-format/movie_atoms), [MP4 standard](https://www.iso.org/standard/79110.html).

**MP4** is often chosen for delivery and playback: phones, browsers, and televisions generally recognize the container. Its strength is broad compatibility *with suitable contents*. A valid MP4 may still fail to play if the device cannot handle its codec, profile, bit depth, or audio. “Save as MP4” does not mean “make compatible with every device.”

**MOV** is common in camera recording and editing workflows. It is especially useful for retaining a professional video stream such as ProRes. Media Unseal has a confirmed route to copy ProRes into a separate MOV without re-encoding. This preserves the source codec but does not make the file small: size depends on the video itself. MOV does not automatically imply high quality and may be less convenient for a device expecting MP4.

**How to choose.** If you need the source stream, start with an allowed copy route and inspect the result. If a device requires another codec, choose **Video format**, then **Configure** and the required encoder. Re-encoding takes time and may change size and quality, but can create a new compatible combination. Media Unseal saves selected video and audio tracks **to separate files**: an extracted video stream in MP4 or MOV does not automatically gain sound.

After saving, check duration, seeking, and playback through the file. For delivery, follow the recipient's requirements, rather than relying only on the extension or a successful export.

### 3.3. MKV / Matroska

Matroska is an open, extensible format; **MKV** is the familiar extension for its video files. Its name suggests a nested structure: a source container may hold several video, dubbed audio, and subtitle streams, along with language labels, chapters, and attached files. EBML is its technical foundation. A related audio-only file often has the MKA extension. [Matroska basics](https://www.matroska.org/technical/basics.html), [container elements](https://www.matroska.org/technical/elements.html).

This flexibility is useful for keeping sources with several languages and for preserving streams without re-encoding. Media Unseal has confirmed routes for H.264, HEVC, AV1, and FFV1 to **separate MKV files with a single video stream**. The application extracts selected streams individually; it does not assemble them into a new multitrack MKV. The container is not inherently lossless: its video may use either lossy or lossless compression.

**Attachments** differ from time-based media streams. Matroska can store cover images, for instance. In the application you can open them through **View**, select them individually, or click **Extract all**. Its own C# module reads the Matroska structure and copies suitable JPEG/PNG files without changing their bytes. It is not a video encoder or a general-purpose MKV editor. Other attachment types do not become supported just because Matroska can store them.

MKV is a sensible choice when the original codec, multiple tracks, or archival use matters. But a particular player may not understand an individual codec, subtitle type, or attachment. For delivery to someone else, test the output in their application or device.

### 3.4. AVI and WebM

**AVI** is a RIFF-family container familiar from older Windows software, camera archives, and editing files. It holds stream descriptions, data, and an index for seeking. Today its practical value often comes from devices or applications that specifically expect AVI. Putting a modern codec into AVI will not make it intelligible to an older device. [AVI structure](https://learn.microsoft.com/en-us/windows/win32/directshow/avi-riff-file-reference).

Media Unseal has confirmed copy routes for MPEG-4 Part 2 and MJPEG video into AVI. MJPEG stores frames as separate JPEG-like images and, under comparable conditions, often needs more space than modern interframe codecs. But **AVI does not mean uncompressed video**: the stream inside determines compression. For an older device, check its permitted codec, audio, dimensions, and frame rate.

**WebM** was created for web delivery and is based on Matroska, but has stricter content rules. The application has confirmed copy routes for VP8 and VP9 into WebM; for new output, the dynamic catalog checks the available FFmpeg options. Typical WebM audio codecs include Vorbis and Opus. Support for a codec in MKV does not prove it is permitted in WebM. [WebM overview](https://www.webmproject.org/about/), [container rules](https://www.webmproject.org/docs/container/).

Choose AVI when an older workflow requires it, and WebM for suitable web codecs. Their size and quality depend on codecs and settings, not container names. If copying into the desired format is rejected, choose a compatible encoder and test the result in the target environment.

### 3.5. Other Containers

Beyond MP4, MOV, MKV, AVI, and WebM, the application offers several more **candidate video containers**. They appear when the installed FFmpeg reports the required muxer and encoder; the application also checks the specific combination. For an original video stream, Media Unseal can preserve a detected source container without re-encoding when the copy route applies. None of these formats “compresses more” on its own; the stream inside determines size.

| Container | Main purpose and feature | Practical guidance |
|---|---|---|
| **MPEG-TS (.ts)** | A transport stream for broadcasting and delivery in segments. Useful for sequential data delivery; as a file archive it may provide less familiar metadata and less convenient navigation than MP4 or MKV. | Choose it if the recipient expects TS. Do not confuse the TS container with MPEG-2 video; it can contain different permitted streams. |
| **MPEG-PS (.mpg/.mpeg)** | An older program stream associated with MPEG video and disc workflows, often found in older collections. | The extension alone does not determine compatibility; check the codec and player requirements. |
| **MXF (.mxf)** | Professional video exchange and production. It can carry workflow metadata; specific MXF variants may impose strict requirements on frame rate, codec, and track structure. | Use it when an editing or broadcast system requires it. Open the output in the target application to confirm it works. |
| **FLV (.flv)** | The historical Flash Video container. Today it is more often needed to read or work with legacy material than for new file delivery. | Moving it into a modern format may need only a container change or may require a new codec. |
| **ASF (.asf)** | The Windows Media family, with its own metadata and streaming history. It appears in older recording and playback systems. | ASF itself does not guarantee support for a particular audio or video codec. |
| **Ogg Video (.ogv)** | A video use of the open Ogg container, historically associated with Theora and Vorbis. | Appropriate if the recipient specifically accepts Ogg Video; consider WebM for web compatibility too. |
| **3GP (.3gp) and 3G2 (.3g2)** | Mobile containers designed around the limits of older phones and networks. They may require modest resolution, frame rate, and specific codecs. | Mainly useful for a particular older device or archive, not as universal MP4 substitutes. |
| **NUT (.nut)** | A technical container from the FFmpeg ecosystem intended for flexible stream storage. | Useful in a specialized workflow; check support in ordinary players and devices separately. |

**How to read the catalog below.** The earlier audit listed **66 video outputs from a local FFmpeg build**, including the five main containers. The remaining **60 entries** are kept here for completeness. This is a list of *that FFmpeg build's capabilities*, not 60 guaranteed options in the Media Unseal menu. Only some are video containers in the application's current catalog; the rest include elementary streams, images, manifests, and special formats. “Default codec” reflects FFmpeg output from the audit; it is **neither the only permitted codec nor a confirmed combination**. The same extension used by different muxers does not imply identical file structure.

#### Ordinary media files

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.3g2` | `3g2` | 3GPP2 mobile container; default codec `h263` |
| `.3gp` | `3gp` | 3GPP mobile container; default codec `h263` |
| `.amv` | `amv` | Legacy portable-player format; default codec `amv` |
| `.asf` | `asf` | Windows Media container; default codec `msmpeg4v3` |
| `.f4v` | `f4v` | Flash MP4 container; default codec `h264` |
| `.flv` | `flv` | Flash Video container; default codec `flv1` |
| `.ismv` | `ismv` | Fragmented MP4 for Smooth Streaming; default codec `h264` |
| `.mpg` | `mpeg` | MPEG program stream; default codec `mpeg1video` |
| `.ogg` | `ogg` | Ogg container; default codec `theora` |
| `.ogv` | `ogv` | Ogg video file; default codec `theora` |
| `.ts` | `mpegts` | MPEG-TS transport stream; default codec `mpeg2video` |
| `.wtv` | `wtv` | Windows TV recording container; default codec `mpeg2video` |

#### Elementary streams

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.a64` | `a64` | Commodore 64 graphics stream; default codec `a64_multi` |
| `.apv` | `apv` | APV codec stream; default codec `apv` |
| `.avs` | `avs2` | AVS2 elementary stream; default codec `avs2` |
| `.dnxhd` | `dnxhd` | DNxHD editing stream; default codec `dnxhd` |
| `.drc` | `dirac` | Dirac elementary stream; default codec `dirac` |
| `.evc` | `evc` | EVC video stream; default codec `evc` |
| `.h261` | `h261` | H.261 elementary stream; default codec `h261` |
| `.h263` | `h263` | H.263 elementary stream; default codec `h263` |
| `.h264` | `h264` | H.264 elementary stream; default codec `h264` |
| `.hevc` | `hevc` | HEVC elementary stream; default codec `hevc` |
| `.ivf` | `ivf` | Simple video stream container; default codec `vp8` |
| `.m2v` | `mpeg2video` | MPEG-2 elementary stream; default codec `mpeg2video` |
| `.m4v` | `m4v` | MPEG-4 video file; default codec `mpeg4` |
| `.mjpg` | `mjpeg` | JPEG-frame stream; default codec `mjpeg` |
| `.mjpg` | `mpjpeg` | JPEG-frame stream; default codec `mjpeg` |
| `.mpg` | `mpeg1video` | MPEG program stream; default codec `mpeg1video` |
| `.obu` | `obu` | AV1 elementary OBUs; default codec `av1` |
| `.pdv` | `pdv` | Specialized PDV stream; default codec `pdv` |
| `.vvc` | `vvc` | VVC elementary stream; default codec `vvc` |
| `.y4m` | `yuv4mpegpipe` | Uncompressed YUV4MPEG frame sequence; default codec `wrapped_avframe` |
| `.yuv` | `rawvideo` | Raw YUV video data; default codec `rawvideo` |

#### Images and animations

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.aif` | `aiff` | AIFF audio file with possible cover art; default codec `png` |
| `.apng` | `apng` | Animated PNG image; default codec `apng` |
| `.avif` | `avif` | AV1-based image; default codec `av1` |
| `.bmp` | `image2` | Bitmap image; default codec `mjpeg` |
| `.fits` | `fits` | Scientific bitmap image; default codec `fits` |
| `.flac` | `flac` | FLAC audio; an image may be cover art; default codec `png` |
| `.gif` | `gif` | Still or animated GIF image; default codec `gif` |
| `.ico` | `ico` | Icon file; default codec `bmp` |
| `.mp3` | `mp3` | MP3 audio; an image may be cover art; default codec `png` |
| `.webp` | `webp` | WebP image, possibly animated; default codec `webp` |

#### Streaming formats and manifests

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.asf` | `asf_stream` | Windows Media container; default codec `msmpeg4v3` |
| `.m3u8` | `hls` | HLS streaming playlist; default codec `h264` |
| `.mpd` | `dash` | DASH streaming manifest; default codec `h264` |

#### Specialized formats

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.cpk` | `film_cpk` | Sega FILM/CPK container; default codec `cinepak` |
| `.dv` | `dv` | DV digital video; default codec `dvvideo` |
| `.dvd` | `dvd` | DVD Video stream; default codec `mpeg2video` |
| `.flm` | `filmstrip` | Filmstrip frame sequence; default codec `rawvideo` |
| `.gxf` | `gxf` | Professional GXF exchange container; default codec `mpeg2video` |
| `.m4v` | `ipod` | MPEG-4 video file; default codec `h264` |
| `.mxf` | `mxf` | Professional MXF editing container; default codec `mpeg2video` |
| `.mxf` | `mxf_opatom` | Professional MXF editing container; default codec `dnxhd` |
| `.nut` | `nut` | NUT container; default codec `mpeg4` |
| `.rm` | `rm` | RealMedia container; default codec `rv10` |
| `.roq` | `roq` | Legacy RoQ game format; default codec `roq` |
| `.swf` | `swf` | Flash container; default codec `flv1` |
| `.vob` | `svcd` | DVD/SVCD stream; default codec `mpeg2video` |
| `.vob` | `vob` | DVD/SVCD stream; default codec `mpeg2video` |

An entry in this list becomes a practical application output only when the interface offers that type, the installed FFmpeg supplies the necessary components, and the combination passes its check. Confirmed copy routes are summarized in article 3.8 and reference table 6.2.

### 3.6. Audio Containers

An audio file also has **packaging and contents**. WAV is not a synonym for “perfect sound,” while MP3 names both a common codec and a file with a particular structure. Audio may be uncompressed, losslessly compressed, or lossy. Choosing another container will not restore details already lost from the source. Copying preserves the source audio codec; conversion decodes and writes the audio again.

| File or family | Typical contents and reason to choose it | What matters in use |
|---|---|---|
| **WAV (.wav)** | A RIFF container, often with uncompressed PCM. Useful for editing, exchange with an audio editor, and older hardware compatibility. | PCM usually needs considerable space; WAV can hold other codecs too. Check sample rate, bit depth, and channels if the receiving application has requirements. |
| **AIFF (.aif/.aiff)** | An Apple-family audio format, often with PCM. Similar to WAV in its uses, but different in structure and traditional ecosystem. | Switching WAV to AIFF without changing the codec does not make audio “cleaner.” Choose according to the editor you use. |
| **CAF (.caf)** | A flexible Core Audio container. Media Unseal provides PCM and ALAC options for it, depending on FFmpeg. | Useful in certain Apple workflows; check support elsewhere. ALAC compresses losslessly, while PCM usually stores samples without such compression. |
| **AU (.au)** | A historical audio format found in older systems and archives. | Use for compatibility with existing material, rather than choosing it by habit over widely accepted WAV or FLAC. |
| **M4A (.m4a)** | An audio use of the MP4 family. Often carries lossy AAC or lossless ALAC. | M4A does not reveal which codec is inside. Check stream properties for device compatibility. |
| **MP3 (.mp3)** | A widely distributed lossy audio format with extensive support. | Convenient for playback and sharing, but re-encoding an already compressed source does not improve it. |
| **FLAC (.flac)** | A **lossless** compression format with its own metadata blocks; it can hold cover art. | Usually saves substantial space versus uncompressed PCM while preserving recoverable samples, but the actual saving depends on the material. The device must support FLAC. |
| **Ogg (.ogg/.oga)** | An open container able to carry different audio streams. .ogg is often associated with Vorbis; .oga emphasizes audio content. | Ogg is the packaging; Vorbis is a codec. The extension alone does not identify the audio inside. |
| **Opus (.opus)** | An Opus stream in Ogg packaging, useful for speech, music, and limited bitrates. | Opus is lossy. Player support varies, especially on older devices. |
| **AAC / ADTS (.aac)** | An AAC stream with ADTS transport headers, a compact package distinct from M4A. | Useful for extracting AAC without re-encoding, but it normally carries less familiar collection metadata than M4A. |
| **AC-3 (.ac3) and E-AC-3 (.eac3)** | Separate streams of Dolby-family multichannel audio. | Often used to retain the original track; check support for the exact variant and channel layout on the target device. |
| **DTS (.dts)** | A separate DTS multichannel audio stream. | Useful for a home theater, but not every player or television decodes it. |
| **Other containers with audio** | AVI, MKV, MOV, MP4, WebM, and other video files may also carry audio tracks. | Media Unseal extracts a selected audio track **as a separate file**; it need not retain the source file's container. |

The current audio conversion catalog is limited to supported types: MP3, WAV, FLAC, AAC/ADTS, AC-3, E-AC-3, DTS, Ogg, Opus, M4A, AIFF, AU, and CAF. Whether a particular output appears depends on the installed FFmpeg and a suitable encoder; the application checks the selected combination. AAC, MP3, FLAC, Opus, Vorbis, AC-3, E-AC-3, DTS, certain PCM types, and ALAC also have extraction routes without re-encoding. A standalone audio source in some formats can retain its original packaging. [FLAC format](https://datatracker.ietf.org/doc/html/rfc9639), [Ogg structure](https://www.xiph.org/ogg/doc/oggstream.html), [Opus in Ogg](https://datatracker.ietf.org/doc/html/rfc7845).

**How to choose.** For an unchanged track, leave **Original — no transcoding** selected. For uncompressed working audio, WAV with PCM is common; for compact lossless storage, FLAC or compatible ALAC; for a small, widely supported file, MP3 or AAC; for modern applications, Opus when the recipient accepts it. These are guidelines, not guarantees: check the codec, channels, and output on the intended device.

The **85 audio outputs** listed by the earlier audit of a local FFmpeg build are retained below. This is *its* catalog, not 85 tested Media Unseal buttons. It includes video containers with a declared default audio codec, raw streams, and niche formats. The “default codec” column does not promise that every source or encoder will work.

#### Ordinary media files

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.3g2` | `3g2` | 3GPP2 mobile container; default codec `amr_nb` |
| `.3gp` | `3gp` | 3GPP mobile container; default codec `amr_nb` |
| `.aac` | `adts` | Elementary AAC audio; default codec `aac` |
| `.ac3` | `ac3` | Dolby Digital audio; default codec `ac3` |
| `.aif` | `aiff` | AIFF audio with possible cover art; default codec `pcm_s16be` |
| `.amr` | `amr` | AMR narrowband speech; default codec `amr_nb` |
| `.asf` | `asf` | Windows Media container; default codec `wmav2` |
| `.au` | `au` | Classic AU audio file; default codec `pcm_s16be` |
| `.caf` | `caf` | Core Audio container; default codec `pcm_s16be` |
| `.flac` | `flac` | FLAC audio, possibly with cover art; default codec `flac` |
| `.m4v` | `ipod` | MPEG-4 video file; default audio codec `aac` |
| `.mkv` | `matroska` | Matroska container; default audio codec `vorbis` |
| `.mov` | `mov` | QuickTime container; default audio codec `aac` |
| `.mp3` | `mp3` | MP3 audio, possibly with cover art; default codec `mp3` |
| `.mp4` | `mp4` | MPEG-4 container; default audio codec `aac` |
| `.mp4` | `psp` | MPEG-4 container; default audio codec `aac` |
| `.oga` | `oga` | Ogg audio file; default codec `flac` |
| `.ogg` | `ogg` | Ogg container; default codec `vorbis` |
| `.opus` | `opus` | Opus audio; default codec `opus` |
| `.spx` | `spx` | Speex speech; default codec `speex` |
| `.w64` | `w64` | WAV with extended size limits; default codec `pcm_s16le` |
| `.wav` | `wav` | WAV audio container; default codec `pcm_s16le` |
| `.webm` | `webm` | WebM container; default audio codec `opus` |
| `.wv` | `wv` | Lossless WavPack audio; default codec `wavpack` |

#### Elementary streams

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.302` | `daud` | Professional D-10 audio stream; default codec `pcm_s24daud` |
| `.al` | `alaw` | Raw A-law data; default codec `pcm_alaw` |
| `.aptx` | `aptx` | aptX stream; default codec `aptx` |
| `.aptxhd` | `aptx_hd` | aptX HD stream; default codec `aptx_hd` |
| `.c2` | `codec2` | Codec2 speech stream; default codec `codec2` |
| `.dfpwm` | `dfpwm` | Specialized DFPWM stream; default codec `dfpwm` |
| `.dts` | `dts` | DTS multichannel audio; default codec `dts` |
| `.eac3` | `eac3` | Dolby Digital Plus audio; default codec `eac3` |
| `.g722` | `g722` | G.722 speech stream; default codec `adpcm_g722` |
| `.gsm` | `gsm` | GSM speech stream; default codec `gsm` |
| `.latm` | `latm` | AAC LATM transport stream; default codec `aac` |
| `.lbc` | `ilbc` | iLBC speech stream; default codec `ilbc` |
| `.lc3` | `lc3` | LC3 speech stream; default codec `lc3` |
| `.mlp` | `mlp` | MLP audio; default codec `mlp` |
| `.mp2` | `mp2` | MPEG Layer II audio; default codec `mp2` |
| `.rso` | `rso` | Legacy RSO audio; default codec `pcm_u8` |
| `.sb` | `s8` | Raw 8-bit audio samples; default codec `pcm_s8` |
| `.sbc` | `sbc` | SBC audio; default codec `sbc` |
| `.spdif` | `spdif` | S/PDIF digital audio transport; default codec `ac3` |
| `.sw` | `s16le` | Raw 16-bit audio samples; default codec `pcm_s16le` |
| `.thd` | `truehd` | Dolby TrueHD audio; default codec `truehd` |
| `.ub` | `u8` | Raw 8-bit audio samples; default codec `pcm_u8` |
| `.ul` | `mulaw` | Raw μ-law data; default codec `pcm_mulaw` |
| `.uw` | `u16le` | Raw 16-bit audio samples; default codec `pcm_u16le` |

#### Streaming formats and manifests

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.asf` | `asf_stream` | Windows Media container; default audio codec `wmav2` |
| `.m3u8` | `hls` | HLS streaming playlist; default audio codec `aac` |
| `.mpd` | `dash` | DASH streaming manifest; default audio codec `aac` |

#### Specialized formats

| Extension | FFmpeg muxer | Purpose or feature |
|---|---|---|
| `.adx` | `adx` | ADX game audio; default codec `adpcm_adx` |
| `.amv` | `amv` | Legacy portable-player format; default audio codec `adpcm_ima_amv` |
| `.apm` | `apm` | APM game audio; default codec `adpcm_ima_apm` |
| `.ast` | `ast` | Specialized AST container; default codec `pcm_s16be_planar` |
| `.aud` | `wsaud` | Westwood game audio; default codec `adpcm_ima_ws` |
| `.avi` | `avi` | AVI container; default audio codec `mp3` |
| `.cpk` | `film_cpk` | Sega FILM/CPK container; default codec `pcm_s16be_planar` |
| `.dv` | `dv` | DV digital video; default audio codec `pcm_s16le` |
| `.dvd` | `dvd` | DVD Video stream; default audio codec `mp2` |
| `.f4v` | `f4v` | Flash MP4 container; default audio codec `aac` |
| `.flv` | `flv` | Flash Video container; default audio codec `mp3` |
| `.gxf` | `gxf` | Professional GXF exchange container; default audio codec `pcm_s16le` |
| `.iamf` | `iamf` | IAMF immersive audio container; default codec `opus` |
| `.ismv` | `ismv` | Fragmented MP4 for Smooth Streaming; default audio codec `aac` |
| `.mmf` | `mmf` | SMAF mobile-device audio; default codec `adpcm_yamaha` |
| `.mpg` | `mpeg` | MPEG program stream; default audio codec `mp2` |
| `.mxf` | `mxf` | Professional MXF editing container; default audio codec `pcm_s16le` |
| `.mxf` | `mxf_opatom` | Professional MXF editing container; default audio codec `pcm_s16le` |
| `.nut` | `nut` | NUT container; default audio codec `vorbis` |
| `.ogv` | `ogv` | Ogg video file; default audio codec `vorbis` |
| `.rm` | `rm` | RealMedia container; default audio codec `ac3` |
| `.roq` | `roq` | Legacy RoQ game format; default audio codec `roq_dpcm` |
| `.sf` | `ircam` | IRCAM audio; default codec `pcm_s16le` |
| `.sox` | `sox` | SoX audio; default codec `pcm_s32le` |
| `.swf` | `swf` | Flash container; default audio codec `mp3` |
| `.tco` | `g723_1` | G.723.1 speech stream; default codec `g723_1` |
| `.ts` | `mpegts` | MPEG-TS transport stream; default audio codec `mp2` |
| `.tta` | `tta` | Lossless TTA audio; default codec `tta` |
| `.tun` | `alp` | Specialized ALP audio; default codec `adpcm_ima_alp` |
| `.vag` | `kvag` | VAG game audio; default codec `adpcm_ima_ssi` |
| `.vob` | `svcd` | DVD/SVCD stream; default audio codec `mp2` |
| `.vob` | `vob` | DVD/SVCD stream; default audio codec `mp2` |
| `.voc` | `voc` | Legacy VOC audio; default codec `pcm_s16le` |
| `.wtv` | `wtv` | Windows TV recording container; default audio codec `ac3` |

An FFmpeg capability in this list does not mean the output appears in the application's menu. For a format provided by the interface, the encoder and compatibility are checked. Some rows are video containers with a default audio codec.

### 3.7. Image Formats

An image format determines how pixels, color, transparency, and metadata are stored. Some formats can hold a **sequence of frames**. When choosing one, the main distinctions are lossy versus lossless compression, transparency support, and animation. An extension does not guarantee that a particular file uses every feature of its format. During analysis, Media Unseal distinguishes **Image** from **Animation**: multiframe GIF, APNG, and WebP belong to the latter, while an ordinary still PNG belongs to the former. An FPS value, which FFprobe may report even for a still image, does not prove animation by itself. [PNG and APNG specification](https://www.w3.org/TR/png/), [WebP container](https://developers.google.com/speed/webp/docs/riff_container).

**Principal formats and when to use them**

| Format | Properties and typical choice | What the current application does |
|---|---|---|
| **JPEG / JPG** | Lossy compression for photographs; usually smaller than an uncompressed bitmap, but repeated saving may accumulate artifacts. Standard JPEG has no transparency. | Recognizes and extracts suitable streams or cover art. JPG is among the still-image conversion candidates when an FFmpeg encoder is present. |
| **PNG** | Lossless compression, crisp edges, text, and transparency. Useful for diagrams, screenshots, and cover art; a photograph as PNG is often larger than JPEG. | Recognizes a still PNG as an image, extracts a suitable stream or attachment, and offers PNG as a candidate still output. |
| **WebP** | Can be lossy or lossless and supports transparency and animation. Useful for web graphics when the recipient supports it. | Classifies still WebP as an image and animated WebP as animation. There is an original-copy route for a suitable stream and conversion candidates; results depend on FFmpeg and the file. |
| **BMP** | A simple bitmap format, often without efficient compression. Useful for older graphics software compatibility, but usually not ideal for transferring large images. | Recognition and an original extraction route for a suitable stream; candidate still conversion. |
| **TIFF** | Flexible scanning, printing, and intermediate format with multiple possible compression methods and bit depths. The TIFF extension does not promise either uncompressed or lossless contents. | Recognition, original extraction of a suitable stream, and candidate still conversion. |
| **QOI** | A simple lossless bitmap format. Fast encoding is useful in specialized workflows, but mainstream support is narrower than PNG. | Recognition, original extraction of a suitable stream, and candidate still conversion. |
| **GIF** | A palette of up to 256 colors per frame, simple transparency, and frame-based animation. Video with many shades often produces a large GIF or loses colors. | A single-frame GIF is an image; a multiframe GIF is animation. A suitable stream can be saved in its original form; **short GIF creation from video was manually verified**. |
| **APNG** | Extends PNG with frame-based animation, full-color frames, and transparency. An ordinary PNG viewer may display only a still. | A multiframe APNG is classified as animation and has an original extraction route and a conversion candidate. A PNG without animation data does not become animation. |

JPEG and PNG originated in different practical needs: JPEG targets efficient storage of photographs, while PNG was designed for portable lossless graphics as an alternative to GIF. GIF remained useful for simple animation; APNG added frames to PNG, and WebP combined still and animated graphics with several compression methods. [PNG history](https://www.libpng.org/pub/png/pnghist.html), [WebP capabilities](https://developers.google.com/speed/webp/faq).

**What “support” means here.** The current code has original extraction routes for suitable JPEG, PNG, BMP, TIFF, QOI, still WebP, and GIF, as well as animated GIF, APNG, and WebP. Still conversion outputs JPG, PNG, BMP, TIFF, QOI, WebP, and GIF, and animation outputs GIF, APNG, and WebP, appear when the installed FFmpeg supplies the required muxers and encoders and the selected combination passes its check. The application's own module copies separate JPEG/PNG Matroska attachments. **The application does not turn a multiframe GIF or other animation into a still image in this category**: animation conversion keeps a frame sequence or turns it into suitable video.

**Other recognized images**

| Format | Typical use | Media Unseal boundary |
|---|---|---|
| **JPEG 2000** | A compression family for archival, professional, and specialized uses, with lossy and lossless variants. | The format name may be recognized, but an original extraction or still-output route has not been confirmed. |
| **JPEG XL** | Modern image compression with lossy and lossless modes. | Recognition of its name does not imply available application export. |
| **AVIF** | An AV1-based image that may save space, while decoding and support depend on the application. | Recognition is possible; AVIF output is an FFmpeg capability from the audit, not a confirmed graphics output in the interface. |
| **OpenEXR** | Professional graphics with high dynamic range and high numerical precision. | Recognition is possible; general extraction and conversion are unconfirmed. |
| **SVG** | A vector description of shapes rather than an ordinary pixel grid. | FFprobe may report a codec name; rendering and exporting SVG in the application are unconfirmed. |
| **PBM, PGM, PPM, PAM** | The Netpbm family: monochrome, grayscale, color, and extended bitmap graphics. | Recognition and properties may be available, but not every variant has a ready export route. |

**FFmpeg catalog from the earlier audit.** The previous list of 66 video outputs also included the graphics entries below. They do not, by themselves, promise a button in the application: some describe cover art *inside an audio file*, not creation of an image. Distinguish the muxer and its declared codec from the stream actually selected.

| Extension | FFmpeg muxer | What the audit entry means |
|---|---|---|
| .aif | aiff | An AIFF audio file may carry cover art; AIFF is not a graphics file. |
| .apng | apng | Animated PNG output, given a suitable encoder. |
| .avif | avif | AVIF graphics output in FFmpeg; the current application's still catalog does not offer it. |
| .bmp | image2 | Still BMP output through the image sequence muxer. |
| .fits | fits | Specialized scientific data format, not a confirmed application graphics output. |
| .flac | flac | FLAC audio may carry cover art; FLAC is not a graphics file. |
| .gif | gif | GIF output; original extraction and short creation from video are described above. |
| .ico | ico | Icon file; not a confirmed graphics output in the interface. |
| .mp3 | mp3 | MP3 audio may carry cover art; MP3 is not a graphics file. |
| .webp | webp | WebP output; still and animated contents require different choices. |

For a practical decision, first check the row's category and status, then choose **Image format** or **Animation format**. Even if FFprobe recognizes a format, the stream status will show whether a valid export route is missing. A format appearing in FFmpeg does not guarantee a result with every encoder.

### 3.8. Container and Codec Compatibility

Compatibility is not a single yes-or-no permission. **Four conditions** must align: the container accepts the stream type; FFmpeg can write the selected codec into it; Media Unseal has a suitable route and successfully checks it; and the recipient's device or application can open the output. Stream properties add further constraints: profile, pixel format, resolution, frame rate, channel count, headers, and timestamps. Two files with the same extension can therefore behave differently. [FFmpeg formats and muxers](https://ffmpeg.org/ffmpeg-formats.html).

**Start with the task, not the extension.** If you need the original codec and quality, leave **Original — no transcoding** and inspect the proposed file. If you need another container, try stream copying: the application checks the combination and, if successful, does not compress video or audio again. If copying is impossible or the recipient needs another codec, open **Configure**, select a format and encoder, and apply the settings. Conversion changes the stream data; lossy compression may lower quality, and a higher bitrate cannot restore previously lost detail.

| Source stream and confirmed route | Why it helps | Limit of the claim |
|---|---|---|
| **H.264, HEVC, AV1, FFV1 → MKV** | Extracts a video stream into a separate MKV without re-encoding. | It does not promise every player understands all four codecs. |
| **VP8, VP9 → WebM** | Preserves a suitable original video stream in a web container. | Device WebM support and the particular stream's properties must be checked separately. |
| **ProRes → MOV** | Retains the original editing-oriented video stream. | The file may be large and unsuitable for a mainstream player. |
| **MPEG-4 Part 2, MJPEG → AVI** | Useful for older collections and workflows. | An older device may require specific codec settings too. |
| **MPEG-2 Video → separate M2V** | Preserves the video stream without another encode. | M2V is an elementary stream, not a normal multitrack video container. |
| **AAC, MP3, FLAC, Opus, Vorbis, AC-3, E-AC-3, DTS, certain PCM types, ALAC → corresponding audio output** | Extracts a separate track in its original codec. | Packaging and extension are chosen for the stream; other packaging may require a copy check. |

A suitable standalone video or audio source can also retain its original container. The table shows **basic confirmed routes**, not every permitted pair. The current dynamic conversion catalog is restricted to the muxer categories implemented in the application; long lists in sections 3.5–3.7 are a snapshot of an earlier local FFmpeg and do not automatically expand the menu.

**Why is a seemingly suitable file still rejected?**

| Situation | Meaning and next step |
|---|---|
| **The container does not accept the source codec** | Copying into that package is impossible. Choose another container or re-encode with a suitable encoder. |
| **FFmpeg lists the encoder, but no test file is produced** | An encoder name alone does not confirm the “encoder + muxer” pair. Try another option or the available encoder search. |
| **Profile, resolution, FPS, or pixel format does not fit** | Even a permitted codec may have properties that the selected packaging or target device cannot accept. Read the specific status and configure the needed conversion. |
| **Timestamp, header, or bitstream structure errors** | The source data may be at fault, not the format name. Open the Diagnostic Report. Another container or re-encoding sometimes helps, but success is not guaranteed. |
| **The file was created, but the recipient cannot play it** | Writing and playback are different checks. Find out what codecs and properties the device accepts, then produce a file to match. |

**Do not conflate a container's capabilities with the application's actions.** MKV can hold many tracks, but Media Unseal saves selected tracks separately; extracted video does not contain audio selected alongside it. WebM is related to MKV but has its own codec restrictions. WAV often contains PCM, but not every WAV is uncompressed. PNG is a lossless image format, while APNG with animation data is a separate use case. File structure, codec, and stream properties together explain compatibility better than an extension alone.

For a specific file, follow a checkable sequence: **analysis → stream type and codec → status → original extraction or format configuration → playback check on the recipient's device**. If the application says **Needs configuration** or **Unavailable**, hover over Status. If a test output or processing fails, open the log and Diagnostic Report. A compatibility table cannot replace those checks when the installed FFmpeg and target device may vary.

## 4. Video and Audio Codecs

### 4.1. What Is a Codec?

A **codec** defines how picture or sound is represented in a digital stream and recovered for playback. An encoder creates a stream according to a codec's rules; a decoder reads it. H.264 is a codec, while libx264 and hardware H.264 NVENC are different encoders. MP4, MKV, and WAV are containers: they package streams but do not themselves determine compression. Two MP4 files may need different decoders.

Uncompressed video stores complete frames; its size grows with resolution, frame rate, and color depth. Uncompressed audio stores each channel's samples. A codec reduces size by describing differences and motion between frames, predicting neighboring values, or discarding details less noticeable to the ear. A decoder reconstructs frames and audio samples for the player. With **lossy** compression, the complete original data cannot be recovered; with **lossless** compression, they can be recovered exactly. PCM and rawvideo normally store data without that kind of compression and need more space.

Codec history reflects the needs of each period. H.261 and H.263 were developed for video calls over limited-bandwidth connections; MPEG-1 and MPEG-2 helped bring digital video to discs and broadcasting. MP3 made music distribution practical over the networks of its time. H.264 became a foundation of mass-market digital video, HEVC offered denser compression for high resolutions, and VP8, VP9, and AV1 developed for online video. New generations do not erase older ones: device requirements, encoding speed, and file size differ. [H.263 history: ITU](https://www.itu.int/rec/T-REC-H.263), [MP3 history: Fraunhofer IIS](https://www.iis.fraunhofer.de/en/magazin/panorama/2025/30-years-of-mp3.html).

For video, **profile, level, bit depth, and pixel format** also matter. Two H.264 streams may play differently on an older device. Audio sample rate and channel count matter too. A high bitrate does not guarantee quality: the result depends on the source, encoder, and settings. Re-encoding compressed material usually adds losses even if the new file is larger.

In Media Unseal, **Original** preserves a supported stream without re-encoding. When you select another format, the application uses an available processing route. The encoder list depends on the installed FFmpeg, and the ability to write a selected codec into a particular container is checked separately. FFmpeg chooses the decoder used to read the source.

### 4.2. H.264 / AVC

**H.264**, also called **AVC** or MPEG-4 Part 10, is a lossy video compression standard. It is widely used in cameras, broadcasts, MP4, and other containers. An encoder keeps reference frames and describes many intermediate frames by their differences and motion. This saves space but makes precise editing and seeking more complex. H.264 is not MPEG-4 Part 2: their similar names refer to different codec generations. [H.264 specification: ITU](https://www.itu.int/rec/T-REC-H.264-202108-S/en).

H.264's main practical advantage is widespread playback. But a file's compatibility also depends on profile, level, resolution, color, audio, and container. A profile defines available encoding features; a level limits stream complexity. An older television may understand H.264 without accepting every profile or level.

Media Unseal has a confirmed route to copy H.264 into MKV without changing the image. Conversion may use software libx264 or an available hardware encoder. The suffixes nvenc, amf, qsv, and vaapi identify implementations of the same codec. A name listed by FFmpeg does not prove the necessary hardware, driver, or combination of settings works. If the source already plays where needed, re-encoding will not improve its quality.

### 4.3. H.265 / HEVC

**H.265**, or **HEVC**, is the video compression generation after H.264. It can retain comparable visual quality at a lower bitrate, especially for complex high-resolution video. The gain depends on the scene, source, and settings: there is no universal percentage saving. More flexible picture partitioning and prediction increase computational complexity. [HEVC specification: ITU](https://www.itu.int/rec/T-REC-H.265/e).

HEVC often appears in 4K, HDR, and footage from modern devices. The name HEVC does not itself mean HDR: bit depth, color properties, and metadata matter. An older device may fail to play HEVC even if it supports the MP4 or MKV container; test the output on the intended player.

Media Unseal has a confirmed route to copy HEVC into MKV without re-encoding. The FFmpeg build examined in the audit listed libx265 and hardware implementations for new encoding; a particular route's availability depends on the system. Converting H.264 to HEVC takes time and does not restore detail already lost in the source.

### 4.4. AV1, VP8, VP9

**VP8** became a video codec of early WebM. **VP9** developed it with more efficient compression and high-resolution capabilities. **AV1** is a separate, newer standard from the Alliance for Open Media, not another version of VP9. All three are normally used for lossy video. [WebM Project](https://www.webmproject.org/about/), [VP9 overview: Google](https://developers.google.com/media/vp9), [AV1 release: AOMedia](https://aomedia.org/press%20releases/the-alliance-for-open-media-kickstarts-video-innovation-era-with-av1-release/).

| Codec | Strength | What to check |
|---|---|---|
| VP8 | Compatibility with early WebM. | At comparable quality it often needs more data than newer codecs. |
| VP9 | Denser compression and widespread web-video use. | Decoding support on the target device. |
| AV1 | Modern efficient compression, including for high resolutions. | Encoding time and the device's hardware support. |

No single percentage describes these comparisons: scenes, bitrate, and settings alter the outcome. WebM is a container, while VP8 and VP9 are codecs within it; AV1 also appears in other containers. AVIF uses AV1-based image coding, but AV1 video and an AVIF image are different use cases.

Media Unseal has confirmed copy routes for VP8 and VP9 into WebM and AV1 into MKV. Copying preserves source quality. For new encoding, only combinations found and checked with the installed FFmpeg are available; a list of hardware implementations alone does not promise that they work.

### 4.5. MPEG-4 Part 2 and Others

**MPEG-4 Part 2** is a video codec familiar from AVI-era files and implementations such as Xvid. **MP4** is a container, while **H.264 / MPEG-4 Part 10** is another codec. MPEG-4 Part 2 generally trails modern codecs in the size-to-quality tradeoff, but older players and archives may still rely on it. Media Unseal has a confirmed route to copy such a stream into AVI.

**H.263** predates H.264 and was designed for video communication over limited-bandwidth connections. The FFmpeg catalog also contains H.261 and H.263+ (h263p). These matter for older recordings and specialized compatibility requirements. The presence of an H.263 encoder in FFmpeg does not mean Media Unseal has a confirmed separate copy route for it. [H.263 Recommendation: ITU](https://www.itu.int/rec/T-REC-H.263).

**MPEG-1 Video** recalls early digital media; **MPEG-2 Video** was widely used on DVDs and in broadcasting. An original MPEG-2 stream has a confirmed output to M2V. **ProRes** targets editing: it typically makes large files useful in post-production, and a copy route to MOV is confirmed. **FFV1** is lossless video for masters and archives, with a confirmed copy route to MKV. H.266/VVC, EVC, and AVS2 appear in the FFmpeg catalog below, but the application has no confirmed route for them. [FFV1: RFC 9043](https://datatracker.ietf.org/doc/html/rfc9043).

### 4.6. AAC, MP3, Opus

**MP3** became established as a way to shrink music files while retaining acceptable sound. Its strength today is compatibility with many older players, car stereos, and household devices. It is lossy: some audio information is discarded with human hearing in mind. A high bitrate may make the losses less noticeable, but does not turn MP3 into lossless audio. [MP3 history: Fraunhofer IIS](https://www.iis.fraunhofer.de/en/magazin/panorama/2025/30-years-of-mp3.html).

**AAC** is a later family of lossy audio coding. It is common in MP4, phone video, and streaming services, and is often more efficient than MP3 under comparable conditions. AAC profiles differ, and an older device may not understand all of them. AAC is a codec; M4A usually names an audio container. The confirmed separate output for original AAC in Media Unseal is AAC/ADTS.

**Opus** was designed for a wide range of uses, from speech and low-latency calls to music. It adapts to different data rates, although older standalone hardware may support it less widely than MP3 or AAC. The application has a confirmed separate output for original Opus. [Opus specification: RFC 6716](https://datatracker.ietf.org/doc/html/rfc6716).

| Priority | Practical choice |
|---|---|
| An older player | MP3, if it supports the file's packaging. |
| Video for common mobile devices | AAC together with suitable video and container. |
| Modern network audio or speech | Opus, if the recipient can play it. |

All three streams can be saved by confirmed routes without re-encoding. Converting MP3 to AAC or Opus does not improve the source sound: lost information stays lost, and new compression may add artifacts. If you only need to extract the track, leave **Original**.

### 4.7. AC3, E-AC3, DTS

**AC-3**, known as Dolby Digital, is a common lossy multichannel audio format. It appears on discs, television recordings, and in video containers. **E-AC-3**, or Dolby Digital Plus, extends the family and supports more flexible multichannel delivery, including online use. A device's AC-3 support does not guarantee it can play E-AC-3. [Dolby Digital Plus: Dolby](https://professional.dolby.com/technologies/dolby-digital-plus/).

**DTS** is a separate family of multichannel coding used in home cinema and on discs. “DTS” in stream information does not reveal every family variant: extensions and modes such as DTS-HD have their own properties. Comparing AC-3 and DTS by bitrate alone is misleading; the source master, codec variant, and playback device matter.

Media Unseal has confirmed routes to copy AC-3, E-AC-3, and DTS into separate files of their respective formats, preserving the source stream structure. Conversion may change the codec, channels, and compatibility. If you need, for example, stereo for a simple device, check channel settings and listen to the output.

### 4.8. FLAC, PCM, and WAV

**PCM** represents sound as a sequence of measured samples. Sample rate, bit depth, and channel count determine how they are stored and how much data they use. Increasing the rate or bit depth of an existing recording adds no missing detail. PCM can use integer or floating-point values; byte order and packaging also vary. This explains the many PCM variants in the FFmpeg catalog. [PCM codecs: FFmpeg](https://ffmpeg.org/ffmpeg-codecs.html).

**WAV** is a container often used for PCM. “Save as WAV” describes the file wrapper, not one codec inside it. Uncompressed PCM needs substantial space but introduces no compression loss and is convenient for editing. Media Unseal has a confirmed separate WAV output for PCM s16le, s24le, s32le, and f32le; this does not automatically cover every PCM variant in the catalog.

**FLAC** compresses PCM losslessly: decoded samples are recovered exactly. It usually needs less space than uncompressed PCM, although savings depend on the recording. Media Unseal has a confirmed route to copy FLAC into a separate FLAC file. Converting MP3 to FLAC does not recover detail lost in MP3; FLAC faithfully preserves only the decoded version. [FLAC specification: RFC 9639](https://datatracker.ietf.org/doc/html/rfc9639).

**ALAC**, **WavPack**, and **TTA** are other lossless audio compression methods. The application has a confirmed separate M4A output for ALAC. Choosing between them usually depends on device and container requirements, not “better” restored sound: with correct decoding, the original samples should match.

### 4.9. Other Codecs

The **catalog of codecs and encoders from the examined FFmpeg build** is retained below. It is a snapshot of one build's capabilities, not a list of Media Unseal outputs guaranteed to work. In the status column, “FFmpeg only” means there is no confirmed copy route in the application. Dynamic conversion may offer some of those codecs after encoder and container compatibility checks; a table entry alone promises no result.

**Reading unfamiliar names.** Huffyuv, FFV1, Ut Video, and MagicYUV are lossless video families used in editing and archiving. rawvideo stores frames with almost no codec compression, so files are large. DNxHD, ProRes, and CineForm (cfhd) serve production editing and exchange. DV Video came from digital camcorders; Theora from the open web ecosystem; WMV and MSMPEG4 from older Windows formats; and Sorenson and Cinepak from early computer video. Similar purposes do not make them interchangeable: containers, color precision, and player support differ.

**Telephony and specialized audio.** AMR-NB and AMR-WB target speech in mobile communication. GSM, G.722, G.723.1, iLBC, and Codec2 are other speech families with different connection requirements. Speex was also designed for speech; Opus covers speech and music. The many ADPCM variants compress differences between samples and are tied to particular devices or older containers: a shared ADPCM prefix does not make them compatible. A-law and μ-law are associated with telephony PCM. aptX, aptX HD, SBC, and LC3 are linked to wireless audio; their encoders appearing in FFmpeg do not turn a file into a ready Bluetooth transmission.

**Graphics beside video.** FFmpeg also classifies individual frames as video codecs: PNG, GIF, APNG, WebP, JPEG, TIFF, and other images. Whether the source is still or contains a frame sequence matters more here than FFprobe's technical category alone. The graphics encoder catalog is not a list of confirmed image conversions in the application. See 3.7 and 1.9 for files and animation.

The “Purpose or feature” column provides a short orientation, not a specification of every profile and variant. For an unusual stream, source properties, selected output, and the actual FFmpeg check remain decisive.

#### Video

| Codec | Encoders in the examined FFmpeg build | Purpose or feature | Status in Media Unseal |
|---|---|---|---|
| `amv` | `amv` | video for portable AMV players | FFmpeg only; route unconfirmed |
| `apv` | `liboapv` | professional APV video codec | FFmpeg only; route unconfirmed |
| `asv1` | `asv1` | legacy ASUS V1 video codec | FFmpeg only; route unconfirmed |
| `asv2` | `asv2` | legacy ASUS V2 video codec | FFmpeg only; route unconfirmed |
| `av1` | `av1_amf`, `av1_d3d12va`, `av1_mf`, `av1_nvenc`, `av1_qsv`, `av1_vaapi`, `av1_vulkan`, `libaom-av1`, `librav1e`, `libsvtav1` | modern video | Copy to MKV |
| `avrp` | `avrp` | Avid 10-bit RGB pixel packing | FFmpeg only; route unconfirmed |
| `avs2` | `libxavs2` | AVS2 video compression standard | FFmpeg only; route unconfirmed |
| `avui` | `avui` | uncompressed Avid Meridien video | FFmpeg only; route unconfirmed |
| `bitpacked` | `bitpacked` | packed uncompressed pixels | FFmpeg only; route unconfirmed |
| `cfhd` | `cfhd` | GoPro CineForm HD editing video | FFmpeg only; route unconfirmed |
| `cinepak` | `cinepak` | early Cinepak computer-video codec | FFmpeg only; route unconfirmed |
| `cljr` | `cljr` | legacy Cirrus Logic packed video | FFmpeg only; route unconfirmed |
| `dirac` | `vc2` | Dirac / VC-2 video codec | FFmpeg only; route unconfirmed |
| `dnxhd` | `dnxhd` | Avid DNxHD editing video | FFmpeg only; route unconfirmed |
| `dvvideo` | `dvvideo` | DV digital-camcorder video | FFmpeg only; route unconfirmed |
| `dxv` | `dxv` | Resolume DXV video for live visuals | FFmpeg only; route unconfirmed |
| `evc` | `libxeve` | MPEG-5 EVC standard | FFmpeg only; route unconfirmed |
| `ffv1` | `ffv1`, `ffv1_vulkan` | lossless video | Copy to MKV |
| `ffvhuff` | `ffvhuff` | FFmpeg lossless Huffyuv variant | FFmpeg only; route unconfirmed |
| `flashsv` | `flashsv` | Flash Screen Video screen recording | FFmpeg only; route unconfirmed |
| `flashsv2` | `flashsv2` | second version of Flash Screen Video | FFmpeg only; route unconfirmed |
| `flv1` | `flv` | early Flash Video / Sorenson Spark | FFmpeg only; route unconfirmed |
| `h261` | `h261` | early H.261 video-call standard | FFmpeg only; route unconfirmed |
| `h263` | `h263` | H.263 video calls over limited-bandwidth links | FFmpeg only; route unconfirmed |
| `h263p` | `h263p` | extended H.263+ variant | FFmpeg only; route unconfirmed |
| `h264` | `h264_amf`, `h264_d3d12va`, `h264_mf`, `h264_nvenc`, `h264_qsv`, `h264_vaapi`, `h264_vulkan`, `libx264`, `libx264rgb` | widely compatible video | Copy to MKV |
| `hap` | `hap` | Hap video for live visuals | FFmpeg only; route unconfirmed |
| `hevc` | `hevc_amf`, `hevc_d3d12va`, `hevc_mf`, `hevc_nvenc`, `hevc_qsv`, `hevc_vaapi`, `hevc_vulkan`, `libx265` | highly compressed video | Copy to MKV |
| `huffyuv` | `huffyuv` | lossless HuffYUV video compression | FFmpeg only; route unconfirmed |
| `magicyuv` | `magicyuv` | lossless MagicYUV video compression | FFmpeg only; route unconfirmed |
| `mpeg1video` | `mpeg1video` | MPEG-1 video for early digital media | FFmpeg only; route unconfirmed |
| `mpeg2video` | `mpeg2_qsv`, `mpeg2_vaapi`, `mpeg2video` | MPEG-2 video | Copy to M2V |
| `mpeg4` | `libxvid`, `mpeg4` | MPEG-4 Part 2 video | Copy to AVI |
| `msmpeg4v2` | `msmpeg4v2` | Microsoft MPEG-4 Part 2 variant, version 2 | FFmpeg only; route unconfirmed |
| `msmpeg4v3` | `msmpeg4` | Microsoft MPEG-4 Part 2 variant, version 3 | FFmpeg only; route unconfirmed |
| `msrle` | `msrle` | legacy Microsoft video with RLE compression | FFmpeg only; route unconfirmed |
| `msvideo1` | `msvideo1` | early Microsoft Video 1 codec | FFmpeg only; route unconfirmed |
| `pdv` | `pdv` | Playdate game-platform video | FFmpeg only; route unconfirmed |
| `prores` | `prores`, `prores_aw`, `prores_ks`, `prores_ks_vulkan` | editing video | Copy to MOV |
| `qtrle` | `qtrle` | QuickTime Animation with RLE compression | FFmpeg only; route unconfirmed |
| `r10k` | `r10k` | uncompressed 10-bit RGB frames | FFmpeg only; route unconfirmed |
| `r210` | `r210` | uncompressed 10-bit RGB frames | FFmpeg only; route unconfirmed |
| `rawvideo` | `rawvideo` | uncompressed frames; large files | FFmpeg only; route unconfirmed |
| `roq` | `roqvideo` | RoQ video for game content | FFmpeg only; route unconfirmed |
| `rpza` | `rpza` | early QuickTime Video / Apple RPZA codec | FFmpeg only; route unconfirmed |
| `rv10` | `rv10` | legacy RealVideo 1.0 | FFmpeg only; route unconfirmed |
| `rv20` | `rv20` | legacy RealVideo 2.0 | FFmpeg only; route unconfirmed |
| `smc` | `smc` | legacy QuickTime Graphics | FFmpeg only; route unconfirmed |
| `snow` | `snow` | experimental Snow video | FFmpeg only; route unconfirmed |
| `speedhq` | `speedhq` | NewTek SpeedHQ editing video | FFmpeg only; route unconfirmed |
| `svq1` | `svq1` | early Sorenson Video 1 | FFmpeg only; route unconfirmed |
| `theora` | `libtheora` | open Theora video codec | FFmpeg only; route unconfirmed |
| `utvideo` | `utvideo` | lossless Ut Video compression | FFmpeg only; route unconfirmed |
| `v210` | `v210` | uncompressed 10-bit 4:2:2 video | FFmpeg only; route unconfirmed |
| `vbn` | `vbn` | Vizrt Binary Image | FFmpeg only; route unconfirmed |
| `vnull` | `vnull` | utility encoder with no usable video | FFmpeg only; route unconfirmed |
| `vp8` | `libvpx`, `vp8_vaapi` | video for WebM | Copy to WebM |
| `vp9` | `libvpx-vp9`, `vp9_qsv`, `vp9_vaapi` | video for WebM | Copy to WebM |
| `vvc` | `libvvenc` | new-generation H.266 / VVC standard | FFmpeg only; route unconfirmed |
| `wmv1` | `wmv1` | legacy Windows Media Video 7 | FFmpeg only; route unconfirmed |
| `wmv2` | `wmv2` | legacy Windows Media Video 8 | FFmpeg only; route unconfirmed |
| `wrapped_avframe` | `wrapped_avframe` | FFmpeg utility frame wrapper | FFmpeg only; route unconfirmed |
| `y41p` | `y41p` | uncompressed YUV 4:1:1 pixel packing | FFmpeg only; route unconfirmed |
| `yuv4` | `yuv4` | uncompressed video with 4:2:0 packing | FFmpeg only; route unconfirmed |
| `zmbv` | `zmbv` | Zip Motion Blocks screen recording compression | FFmpeg only; route unconfirmed |

#### Audio

| Codec | Encoders in the examined FFmpeg build | Purpose or feature | Status in Media Unseal |
|---|---|---|---|
| `aac` | `aac`, `aac_mf` | lossy audio | Copy to AAC |
| `ac3` | `ac3`, `ac3_fixed`, `ac3_mf` | multichannel audio | Copy to AC3 |
| `adpcm_adx` | `adpcm_adx` | ADPCM variant for CRI ADX audio | FFmpeg only; route unconfirmed |
| `adpcm_argo` | `adpcm_argo` | adaptively coded audio | FFmpeg only; route unconfirmed |
| `adpcm_g722` | `g722` | G.722 speech coding | FFmpeg only; route unconfirmed |
| `adpcm_g726` | `g726` | G.726 speech coding | FFmpeg only; route unconfirmed |
| `adpcm_g726le` | `g726le` | G.726 variant with different bit order | FFmpeg only; route unconfirmed |
| `adpcm_ima_alp` | `adpcm_ima_alp` | adaptively coded audio | FFmpeg only; route unconfirmed |
| `adpcm_ima_amv` | `adpcm_ima_amv` | IMA ADPCM variant for AMV | FFmpeg only; route unconfirmed |
| `adpcm_ima_apm` | `adpcm_ima_apm` | adaptively coded audio | FFmpeg only; route unconfirmed |
| `adpcm_ima_qt` | `adpcm_ima_qt` | IMA ADPCM variant for QuickTime | FFmpeg only; route unconfirmed |
| `adpcm_ima_ssi` | `adpcm_ima_ssi` | adaptively coded audio | FFmpeg only; route unconfirmed |
| `adpcm_ima_wav` | `adpcm_ima_wav` | IMA ADPCM variant for WAV | FFmpeg only; route unconfirmed |
| `adpcm_ima_ws` | `adpcm_ima_ws` | adaptively coded audio | FFmpeg only; route unconfirmed |
| `adpcm_ms` | `adpcm_ms` | Microsoft ADPCM variant | FFmpeg only; route unconfirmed |
| `adpcm_swf` | `adpcm_swf` | ADPCM variant for Flash | FFmpeg only; route unconfirmed |
| `adpcm_yamaha` | `adpcm_yamaha` | Yamaha ADPCM variant | FFmpeg only; route unconfirmed |
| `alac` | `alac` | lossless audio | Copy to M4A |
| `amr_nb` | `libopencore_amrnb` | specialized audio codec | FFmpeg only; route unconfirmed |
| `amr_wb` | `libvo_amrwbenc` | specialized audio codec | FFmpeg only; route unconfirmed |
| `anull` | `anull` | specialized audio codec | FFmpeg only; route unconfirmed |
| `aptx` | `aptx` | specialized audio codec | FFmpeg only; route unconfirmed |
| `aptx_hd` | `aptx_hd` | specialized audio codec | FFmpeg only; route unconfirmed |
| `codec2` | `libcodec2` | specialized audio codec | FFmpeg only; route unconfirmed |
| `comfortnoise` | `comfortnoise` | specialized audio codec | FFmpeg only; route unconfirmed |
| `dfpwm` | `dfpwm` | specialized audio codec | FFmpeg only; route unconfirmed |
| `dts` | `dca` | multichannel audio | Copy to DTS |
| `eac3` | `eac3` | multichannel audio | Copy to EAC3 |
| `flac` | `flac` | lossless audio | Copy to FLAC |
| `g723_1` | `g723_1` | specialized audio codec | FFmpeg only; route unconfirmed |
| `gsm` | `libgsm` | specialized audio codec | FFmpeg only; route unconfirmed |
| `gsm_ms` | `libgsm_ms` | specialized audio codec | FFmpeg only; route unconfirmed |
| `ilbc` | `libilbc` | specialized audio codec | FFmpeg only; route unconfirmed |
| `lc3` | `liblc3` | specialized audio codec | FFmpeg only; route unconfirmed |
| `mlp` | `mlp` | specialized audio codec | FFmpeg only; route unconfirmed |
| `mp2` | `libtwolame`, `mp2`, `mp2fixed` | specialized audio codec | FFmpeg only; route unconfirmed |
| `mp3` | `libmp3lame`, `libshine`, `mp3_mf` | lossy audio | Copy to MP3 |
| `nellymoser` | `nellymoser` | specialized audio codec | FFmpeg only; route unconfirmed |
| `opus` | `libopus`, `opus` | lossy audio | Copy to Opus |
| `pcm_alaw` | `pcm_alaw` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_bluray` | `pcm_bluray` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_dvd` | `pcm_dvd` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_f32be` | `pcm_f32be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_f32le` | `pcm_f32le` | uncompressed audio samples | Copy to WAV |
| `pcm_f64be` | `pcm_f64be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_f64le` | `pcm_f64le` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_mulaw` | `pcm_mulaw` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s16be` | `pcm_s16be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s16be_planar` | `pcm_s16be_planar` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s16le` | `pcm_s16le` | uncompressed audio samples | Copy to WAV |
| `pcm_s16le_planar` | `pcm_s16le_planar` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s24be` | `pcm_s24be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s24daud` | `pcm_s24daud` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s24le` | `pcm_s24le` | uncompressed audio samples | Copy to WAV |
| `pcm_s24le_planar` | `pcm_s24le_planar` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s32be` | `pcm_s32be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s32le` | `pcm_s32le` | uncompressed audio samples | Copy to WAV |
| `pcm_s32le_planar` | `pcm_s32le_planar` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s64be` | `pcm_s64be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s64le` | `pcm_s64le` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s8` | `pcm_s8` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_s8_planar` | `pcm_s8_planar` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u16be` | `pcm_u16be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u16le` | `pcm_u16le` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u24be` | `pcm_u24be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u24le` | `pcm_u24le` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u32be` | `pcm_u32be` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u32le` | `pcm_u32le` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_u8` | `pcm_u8` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `pcm_vidc` | `pcm_vidc` | uncompressed audio samples | FFmpeg only; route unconfirmed |
| `ra_144` | `real_144` | specialized audio codec | FFmpeg only; route unconfirmed |
| `roq_dpcm` | `roq_dpcm` | specialized audio codec | FFmpeg only; route unconfirmed |
| `s302m` | `s302m` | specialized audio codec | FFmpeg only; route unconfirmed |
| `sbc` | `sbc` | specialized audio codec | FFmpeg only; route unconfirmed |
| `speex` | `libspeex` | specialized audio codec | FFmpeg only; route unconfirmed |
| `truehd` | `truehd` | specialized audio codec | FFmpeg only; route unconfirmed |
| `tta` | `tta` | specialized audio codec | FFmpeg only; route unconfirmed |
| `vorbis` | `libvorbis`, `vorbis` | lossy audio | Copy to OGG |
| `wavpack` | `wavpack` | specialized audio codec | FFmpeg only; route unconfirmed |
| `wmav1` | `wmav1` | specialized audio codec | FFmpeg only; route unconfirmed |
| `wmav2` | `wmav2` | specialized audio codec | FFmpeg only; route unconfirmed |

#### Graphics formats

| Codec | Encoders in the examined FFmpeg build | Purpose or feature | Status in Media Unseal |
|---|---|---|---|
| `a64_multi` | `a64multi` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `a64_multi5` | `a64multi5` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `alias_pix` | `alias_pix` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `apng` | `apng` | animated PNG | FFmpeg only; route unconfirmed |
| `bmp` | `bmp` | BMP bitmap image | FFmpeg only; route unconfirmed |
| `dpx` | `dpx` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `exr` | `exr` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `fits` | `fits` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `gif` | `gif` | GIF still image or animation | Short GIF creation from video manually confirmed |
| `hdr` | `hdr` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `jpeg2000` | `jpeg2000`, `libopenjpeg` | JPEG-family image format | FFmpeg only; route unconfirmed |
| `jpegls` | `jpegls` | JPEG-family image format | FFmpeg only; route unconfirmed |
| `jpegxl` | `libjxl` | JPEG-family image format | FFmpeg only; route unconfirmed |
| `jpegxl_anim` | `libjxl_anim` | animated JPEG XL image | FFmpeg only; route unconfirmed |
| `jpegxs` | `libsvtjpegxs` | JPEG-family image format | FFmpeg only; route unconfirmed |
| `ljpeg` | `ljpeg` | JPEG-family image format | FFmpeg only; route unconfirmed |
| `mjpeg` | `mjpeg`, `mjpeg_qsv`, `mjpeg_vaapi` | sequence of JPEG frames | Copy video to AVI |
| `pam` | `pam` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `pbm` | `pbm` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `pcx` | `pcx` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `pfm` | `pfm` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `pgm` | `pgm` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `pgmyuv` | `pgmyuv` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `phm` | `phm` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `png` | `png` | lossless PNG image | Extract suitable PNG |
| `ppm` | `ppm` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `qoi` | `qoi` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `sgi` | `sgi` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `sunrast` | `sunrast` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `targa` | `targa` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `tiff` | `tiff` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `wbmp` | `wbmp` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `webp` | `libwebp`, `libwebp_anim` | WebP still image or animation | FFmpeg only; route unconfirmed |
| `xbm` | `xbm` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `xface` | `xface` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `xwd` | `xwd` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |
| `zlib` | `zlib` | bitmap or utility graphics format | FFmpeg only; route unconfirmed |

#### Subtitles in the FFmpeg catalog

| Codec | Encoders in the examined FFmpeg build | Purpose or feature | Status in Media Unseal |
|---|---|---|---|
| `ass` | `ass`, `ssa` | styled text subtitles | Copy to ASS |
| `dvb_subtitle` | `dvbsub` | image-based DVB digital television subtitles | FFmpeg only; route unconfirmed |
| `dvd_subtitle` | `dvdsub` | image-based DVD subtitles | FFmpeg only; route unconfirmed |
| `mov_text` | `mov_text` | text subtitles in MP4/MOV | FFmpeg only; route unconfirmed |
| `subrip` | `srt`, `subrip` | plain text subtitles | Copy to SRT |
| `text` | `text` | plain text subtitle stream | FFmpeg only; route unconfirmed |
| `ttml` | `ttml` | XML-based text subtitles | FFmpeg only; route unconfirmed |
| `webvtt` | `webvtt` | text subtitles for the web | Copy to VTT |
| `xsub` | `xsub` | image-based XSub subtitles | FFmpeg only; route unconfirmed |

PGS is absent from this list of **encoders**, but it has a confirmed copy route to SUP. Image-based subtitles cannot become text without OCR.

### 4.10. Subtitle Codecs

Subtitles also have stream formats, though the word “codec” may sound unusual here. The main distinction is between **text** and **images of captions**. Text tracks store characters and display times; words can be searched, copied, and translated. Image-based tracks store finished pictures of captions; simply changing the format cannot yield text.

**SubRip / SRT** is plain text with timestamps, convenient for exchange but with little styling. **ASS / SSA** add styles, positioning, and effects. **WebVTT** is intended for web video and allows additional display instructions. **mov_text** represents a text track in MP4/MOV-family containers; **TTML** describes subtitles in XML. Conversion between text formats is possible, but not all formatting survives unchanged. [WebVTT: W3C](https://www.w3.org/TR/webvtt1/).

**PGS** is an image-based subtitle format familiar from Blu-ray; **DVD Subtitle**, **DVB Subtitle**, and **XSub** are image-based as well. They preserve the finished appearance of captions, but their words cannot be searched or edited normally. Converting those pictures to text would require optical character recognition (OCR), which Media Unseal does not provide. It therefore does not offer an image-based track as a source for text conversion.

Media Unseal has confirmed copy routes for SubRip to SRT, ASS/SSA to ASS, WebVTT to VTT, and PGS to SUP. Other text outputs may appear after the installed FFmpeg is checked. To keep the original captions and styling, start with **Original**. After a text conversion, check line breaks, styling, and timing in several captions. Encoders named dvdsub, dvbsub, or xsub appearing in FFmpeg do not mean conversion to them is confirmed through the application.

## 5. Processing Technologies

### 5.1. Copying Without Re-encoding

**Stream copying** moves already encoded video, audio, or subtitles into a new file. The application tells FFmpeg which source stream to use and asks it to write that stream without encoding it again. Frames and audio samples are not recalculated, so there is no new generation of compression loss. It is usually faster than conversion because most of the work is reading and writing data. Time still depends on file size, storage speed, and output checks. The output container's structure and some supporting data may differ from the source.

{{diagram:stream-copy}}

In the interface, this is **Original — no transcoding**. Tick the desired streams using the checkboxes on the left, leave them at their original formats, choose a folder, and click **Extract**. Multiple selected streams become separate files. The original codec, frame dimensions, frame rate, audio sample rate, and channels remain unchanged. Copying alone does not reduce quality or repair flaws in the source stream.

The container must accept that codec. For instance, you cannot obtain an arbitrary MP4 from any video stream merely by giving it a .mp4 extension. If the chosen output is incompatible with the source encoding, you need another container or decoding followed by new encoding. Copying also cannot change resolution, FPS, bitrate, or channels. Advanced settings may offer **copy** for a selected container; the application checks compatibility. For video, the final check happens when the output is written. Renaming an extension is no substitute for this check.

There is a subtlety: “without re-encoding” does not always mean that the entire file is byte-for-byte identical. A new container has its own headers and timestamps; some video streams may need technical changes in their bitstream packaging. The compressed picture itself is not created anew. MKV attachments take a separate route: the application's own module copies confirmed JPEG and PNG attachments as the original embedded file bytes.

### 5.2. Decoding and Encoding

During **decoding**, a compressed stream is turned into an internal representation of frames or audio samples. Filters and settings can then change picture dimensions, frame rate, or audio properties as needed. The **encoder** creates a new stream, and a **multiplexer** (muxer) writes it into the output container. This is how conversion works; a demultiplexer (demuxer) reads the source container. A codec defines stream rules; a container packages the stream and related information. [FFmpeg processing overview](https://ffmpeg.org/ffmpeg.html).

{{diagram:transcode}}

Re-encoding is required when the source codec cannot fit the desired container or when you change the stream's own properties. Selecting another format in the application offers a suitable encoder; advanced settings let you choose a different checked option. **Process** starts this conversion. If other tracks in the same task use **Original**, they are not re-encoded simply because the shared button reads Process.

Lossy compression has a cost: a decoder cannot recover details removed by an earlier encode, and a new pass may discard more. Changing format for compatibility can be necessary, but does not “improve” the source quality. An encoder check confirms only a short test on that system, not every real file. After an important conversion, check playback, duration, sound, subtitles, and relevant parts of the picture.

### 5.3. Software Encoders

A **software encoder** performs calculations mainly on the CPU. One codec may have several implementations: for example, libx264 encodes H.264, while the FFmpeg build examined in the audit listed libaom-av1, librav1e, and libsvtav1 for AV1. Audio examples include libmp3lame and libopus. Implementations differ in speed, available settings, and results at particular parameters; a codec name alone does not identify a “best” encoder for every task.

In Media Unseal, the basic list offers a suitable non-hardware option for the selected output format. **Find more encoders** checks additional software options with a short test output. The application verifies that it can write and read the test file. Once you select an encoder, quality, bitrate, and other fields remain optional; CRF is enabled only if its support is confirmed. The real output depends on the source and settings, so a test result does not replace checking the actual export.

High CPU usage during encoding is expected. A slower software encoder can sometimes make a smaller file at similar visible quality, but that is neither a universal rule nor a promise attached to a name. For one file, judge the desired compatibility and the viewing result; for a series, try a small sample first.

### 5.4. Hardware Encoders

**Hardware encoding** moves some work to dedicated units on a graphics card or processor. FFmpeg may include NVIDIA NVENC, Intel QSV, AMD AMF, VAAPI, VideoToolbox, Vulkan, and D3D12VA implementations. They can speed up video creation considerably, but speed and quality depend on hardware generation, driver, codec, and settings. An encoder name in FFmpeg's list does not prove it can start on the current computer.

In video settings, **Hardware encoders** runs a short test encode for the selected format. Results apply to the current machine and that container-and-codec combination. The application checks the test output and also tries to decode video output. A found option can be selected as the encoder and its settings applied. If none is found, check the installed FFmpeg, driver, and hardware support for the codec; a software option may still work when a suitable encoder is available.

**Hardware encoding does not imply an entirely hardware-based processing pipeline.** The current interface has no universal hardware-decoding mode or GPU filters. Some processing may therefore remain on the CPU even when a hardware encoder is used. h264_mf may be present in FFmpeg, but the current hardware search button does not classify it among its hardware options. [Hardware acceleration in FFmpeg documentation](https://ffmpeg.org/ffmpeg.html#Hardware-acceleration).

### 5.5. Bitrate and Quality

**Bitrate** measures how much data a stream uses per second, usually in kbps or Mbps. At a constant 1 Mbps, a minute of video stream occupies roughly 7.5 MB before audio and overhead. This is only an estimate: actual size depends on duration, bitrate variation, and container. A file's overall bitrate combines video, audio, and other streams. Two videos of equal duration and video bitrate may therefore have different total sizes.

With **lossy** compression, an encoder allocates a limited amount of data to frames or audio segments. Raising the bitrate gives it more room for detail, movement, and complex scenes. But one number cannot describe quality: the source, resolution, FPS, codec, settings, repeated encodes, and viewing conditions all matter. Low bitrate may show up as blocks, smeared details, bands in gradients, or audible artifacts. An extremely high bitrate may add little visible benefit while making a file much larger.

**CRF** is a quality-control mode when the video encoder supports it. You set the compression strength, and the encoder spends more data on difficult passages and less on simple ones. For many common software encoders, a lower CRF means fewer losses and a larger file, but the same number across different codecs does not mean the same quality. CRF is not a percentage. Media Unseal enables the field only after confirming that the selected encoder supports it.

**Bitrate** is useful when size limits or platform requirements matter. **CRF** suits cases where consistent perceived quality matters more than knowing the file size in advance. Usually start with one of these controls, create a short sample of a difficult scene, and inspect it on the target screen. The separate audio **Quality, 0–10** field passes a setting to the encoder; its meaning and direction depend on the implementation, so it is not a ten-point rating of the recording. Neither CRF nor new-encoding bitrate applies when copying without re-encoding.

**Lossless compression** restores the input data after decoding but does not necessarily produce a small file. Converting MP3 to FLAC or H.264 to FFV1 merely preserves the already decoded version; earlier losses remain. Use **Original** to retain the original encoded stream itself.

### 5.6. Resolution and FPS

**Resolution** is the number of pixels across and down a frame. 1920 × 1080 means 1,920 points in each row and 1,080 rows, or just over two million pixels per frame. More pixels *can* hold more detail and require more data to process. But enlarging 640 × 360 to 1920 × 1080 cannot create details absent from the source. Camera sharpness, compression, and display size matter too.

A **frame** is one image in a video sequence. **FPS** stands for *frames per second*. At 25 fps, roughly 25 images are displayed each second; at 50 fps, roughly 50. Frame rate affects motion smoothness and the number of frames to encode. Both whole and fractional values occur: 24 and 25, 30 and 60, or approximately 23.976 and 29.97 fps. There is no universally “best” frame rate; it depends on the source, how it was recorded, and playback requirements.

Reducing FPS during conversion drops some frames; increasing it may make FFmpeg repeat neighboring ones. Neither creates new moments of movement like footage actually shot at a higher rate. A large reduction may make motion jerky. Changing FPS may also affect duration or synchronization with complex sources, so check the result. Set the output rate in Media Unseal's video settings, or leave it blank if movement need not change.

Reducing resolution scales frames down and loses some fine detail. Increasing it only recalculates pixels. If you fill only Width or Height, the application asks FFmpeg to calculate the other dimension while preserving proportions. Entering both with a different aspect ratio may stretch the image. Some encoders require dimensions in particular multiples, so the result may be rounded. With **Original**, resolution and FPS stay as they were.

### 5.7. Sample Rate and Channels

Digital audio consists of **samples**, measurements of the signal over time. **Sample rate** counts samples per second for each channel: 44,100 Hz means 44,100 samples each second; 48,000 Hz means 48,000. These values are common in music and video respectively. A higher rate does not, by itself, guarantee audibly better results: recording quality, processing, and equipment matter. Resampling existing audio upward cannot restore missing frequencies.

A **channel** is an individual audio signal. Mono has one and stereo has two. Multichannel recordings may have center, surround, and low-frequency channels. “5.1” usually means five main channels and one low-frequency channel, but the number “6” alone does not fully describe the layout: order and roles within the stream matter. Compatibility also depends on codec, container, and player capabilities.

When conversion reduces the channel count, FFmpeg mixes several input signals into fewer output channels. This changes spatial sound; listen for speech becoming too quiet or channels becoming unbalanced. Increasing the count does not create an independent multichannel recording. Changing sample rate recalculates samples and can limit the upper frequency range. These Media Unseal audio fields are optional; use **Original** to preserve source properties.

### 5.8. Metadata and Languages

**Metadata** is information about a file and its streams apart from the picture, sound, or subtitle text itself. It may include title, performer, date, comments, track title, language, technical parameters, and flags. Some parameters, such as codec and frame size, describe how the stream is built; others are ordinary tags entered by a file's author. Tags may be missing, inaccurate, or out of date after processing.

A track's language is usually a **metadata label**, not a result of automatic speech recognition. It helps distinguish dubbed audio and subtitles, but may be wrong. FFprobe passes found tags and properties to the application, which shows them in the table and stream-properties window. With grouping enabled, Media Unseal places audio and subtitles in language folders; the language can also form part of a safe output name. When no label exists, the application cannot reliably identify the language from the contents.

**Preserve metadata** controls whether source tags are carried over during export. It does not make Media Unseal a metadata editor: there are no controls to correct titles, languages, or other tags manually. Containers do not all support the same fields, so some information may not survive a format change. If metadata matters for your archive, inspect the output file's properties.

### 5.9. Attachment Extraction Module

An MKV can contain **attachments**, separate files embedded in the container, such as cover art, fonts, or related material. They differ from a video track or a frame extracted from video. Media Unseal shows found attachments separately and lets you select them in the attachments window: one, several, or all, including alongside other streams in a shared queue.

For confirmed **JPEG and PNG** attachments, the application uses its own C# module. It reads the Matroska/EBML structure, finds the attachments section, matches names, file types, and order against FFprobe data, and copies the chosen image as its original bytes. This is neither a video decoder nor an image encoder: the embedded JPEG or PNG quality is unchanged. Preview reads with a size limit; exporting a large attachment reads it in parts without loading the entire file into memory.

{{diagram:matroska-attachments}}

The module checks data bounds and whether the source changed after analysis. If an attachment match is ambiguous, the structure is damaged, or data extends beyond the file, it stops instead of selecting a “similar” object. The confirmed route is for JPEG/PNG; other MKV attachments are not automatically extractable by the same method. This is a specialized attachment module, not a general Matroska editor. [Matroska attachments](https://www.matroska.org/technical/attachments.html).

### 5.10. How FFmpeg and FFprobe Work

**FFprobe** answers “what is in this file?” It opens the source and returns information on the container and streams: type, codec, indices, duration, frame size, FPS, audio properties, language, tags, and stream flags where available. Media Unseal requests machine-readable JSON and uses it to fill the stream table, properties, and available actions. Analysis is not a complete frame-by-frame viewing of the entire movie; damaged sections may surface later during processing. [FFprobe documentation](https://ffmpeg.org/ffprobe.html).

**FFmpeg** performs most processing. For copying, it reads a chosen stream and writes it into a compatible container. For conversion, it decodes input, applies transformations if necessary, and passes the result to an encoder. It also handles some image operations and provides video frames for preview. Media Unseal forms task parameters, monitors progress, cancellation, and errors, and displays messages in the log and Diagnostic Report. Its own MKV attachment module is a separate specialized route.

{{diagram:components}}

On first launch, the application checks the presence and readiness of **both** tools. Output formats and encoders are read from the installed FFmpeg, so they may differ on another computer. The application then selects variants appropriate to its workflows and checks individual combinations with short test operations. A successful test shows that the combination worked under those conditions; it does not promise success with every source, complex profile, or damaged file. [FFmpeg formats](https://ffmpeg.org/ffmpeg-formats.html), [FFmpeg encoders](https://ffmpeg.org/ffmpeg-codecs.html).

The Diagnostic Report can show information useful for investigating a failure: operation stage, combination checks, FFmpeg messages, and outcome. This helps distinguish an unsupported codec-and-container pair from a problem with a particular file. FFmpeg and FFprobe are external tools; their version, build contents, drivers, and hardware directly affect the available processing options.

## 6. Reference

### 6.1. Key Terms

Use this glossary for quick lookup. Sections 3–5 explain containers, codecs, and processing in more depth.

| Term | Short meaning |
|---|---|
| **Media file** | A file with picture, sound, subtitles, and supporting information. |
| **Container** | Packaging for streams and metadata, such as MKV, MP4, or WAV. |
| **Extension** | The ending of a file name after the dot; a clue to its type, not proof of its internal codec. |
| **Stream / track** | Separate video, audio, subtitles, image, or another element within a container. |
| **Stream index** | The number used by the application to select a particular source track. |
| **Codec** | Rules for writing and reading stream contents, such as H.264 or FLAC. |
| **Encoder / decoder** | An implementation that creates / reads a stream using a given codec. |
| **Profile / level** | A set of codec capabilities / a limit on stream complexity for hardware. |
| **Lossy compression** | Reducing size by irreversibly removing some information. |
| **Lossless compression** | Reducing size while allowing the original data to be recovered exactly after decoding. |
| **Stream copy** | Writing the original encoded stream to a compatible output without encoding it again. |
| **Conversion / transcoding** | Decoding the source and creating a new stream with an encoder. |
| **Demuxer / muxer** | A component that reads streams from / writes streams into a container. |
| **Bitrate** | Data used per second of a stream, usually measured in kbps or Mbps. |
| **Constant / variable bitrate** | Using data at an approximately set rate / adjusting it to material complexity. |
| **CRF** | A compression-and-quality control parameter for video encoders that support it. |
| **Pixel** | An element of a digital picture. |
| **Color depth** | The number of bits used to represent color values; not the same as audio bit depth. |
| **Alpha channel** | Transparency data, when supported by the format and processing route. |
| **Resolution** | Picture width × height in pixels. |
| **Frame** | One image in a video or animation sequence. |
| **Keyframe** | A frame from which a sequence can be reconstructed without earlier frames. |
| **FPS** | Frames per second, the number of frames shown each second. |
| **Variable frame rate** | A sequence where time intervals between frames vary. |
| **Sample rate** | Audio samples per second for each channel, measured in hertz. |
| **Audio bit depth** | How precisely each PCM sample is represented; not by itself a measure of the source recording's quality. |
| **Channel** | One audio component, such as the left or right signal in stereo. |
| **Downmixing** | Recalculating multichannel audio into fewer output channels. |
| **PCM** | A representation of audio as a sequence of samples. |
| **Metadata / tags** | Information about a file and its streams: titles, languages, dates, and other fields. |
| **Language label** | A track language recorded in the file; it may be missing or wrong. |
| **MKV attachment** | A separate file within Matroska, such as cover art or a font. |
| **MIME type** | A designation of an embedded file's type, such as image/jpeg. |
| **Cover art / poster** | An image associated with a media file, possibly an attachment or a separate stream. |
| **Text / image-based subtitles** | Characters with timestamps / finished pictures of captions. |
| **OCR** | Character recognition in images; not implemented in Media Unseal. |
| **FFprobe** | A tool for analyzing containers and streams. |
| **FFmpeg** | A tool for copying, conversion, and some image operations. |
| **Diagnostic Report** | A detailed history of events and errors used to investigate a problem. |

### 6.2. Compatibility Table

**Compatibility involves several checks:** the application must recognize the source stream, the output container must accept the chosen codec, FFmpeg must have the required encoder for conversion, and the target device must be able to play the result. “MP4 supported” refers only to the container. A precise answer for a particular file also depends on its profile, level, resolution, channels, and condition.

Below are **confirmed routes for preserving the original stream** in Media Unseal. This is not a table of every theoretical FFmpeg pairing. For video in a recognized source container, the application may also preserve that container; otherwise it uses the codec-based route where applicable.

| Source stream | Separate output without re-encoding | What to consider |
|---|---|---|
| H.264, HEVC, AV1, FFV1 | MKV | The codec stays original; playback depends on the player. |
| VP8, VP9 | WebM | WebM accepts these codecs; the container itself does not change quality. |
| MPEG-2 Video | M2V | A separate video stream without the source audio. |
| MPEG-4 Part 2, MJPEG video | AVI | Do not confuse MPEG-4 Part 2 with H.264 or the MP4 container. |
| ProRes | MOV | Large files are common with editing material. |
| AAC, MP3, FLAC, Opus, Vorbis | AAC, MP3, FLAC, Opus, OGG respectively | AAC is written as ADTS; the extension does not determine quality. |
| AC-3, E-AC-3, DTS | AC3, EAC3, DTS | Multichannel audio remains the original stream. |
| PCM s16le, s24le, s32le, f32le | WAV | Other PCM variants are not covered by this confirmed route. |
| ALAC | M4A | The codec remains ALAC. |
| SubRip, ASS/SSA, WebVTT, PGS | SRT, ASS, VTT, SUP respectively | PGS is image-based; its text is not recognized. |
| Still JPEG, PNG, and certain other recognized images | Corresponding original format | Availability depends on the specific stream type. |
| GIF, APNG, animated WebP | Corresponding original format | This preserves a recognized animation rather than converting it to one still frame. |
| JPEG/PNG MKV attachments | JPG/PNG | The application's own module copies a confirmed attachment as a file. |

For a standalone audio source with a recognized container, the application sometimes preserves that original container rather than the fallback output in the table. Suitable WAV, OGG, CAF, AIFF, and M4A are examples. The table describes the main route for extracting a track from another file; the actual output is shown in the interface.

**When another format is needed.** Media Unseal builds its output list from the installed FFmpeg and suggests a suitable encoder. Video candidates include MP4, MKV, MOV, WebM, AVI, MPEG-TS, MPEG, NUT, ASF, FLV, OGV, 3GP, 3G2, and MXF; animation candidates include GIF, APNG, and WebP; audio and text subtitles have their own sets. A catalog entry does not mean every source codec can be copied into that format unchanged or that every encoder will work with it. The application checks the selected combination, and FFmpeg checks the real export. Full container, image, and codec catalogs are in 3.5–3.7 and 4.9.

| Goal | Start with | If it does not fit |
|---|---|---|
| Get an original track | Original and a confirmed container. | Choose another compatible container. |
| Play on a particular device | Find its container, video, and audio requirements. | Convert to a supported combination and test the file on that device. |
| Reduce size | Check the source bitrate and quality. | Configure codec and quality, then compare a sample while accounting for new losses. |
| Preserve the original quality of a compressed stream | Copy without re-encoding. | If another format is required, assess the tradeoff. |

### 6.3. Frequently Asked Questions

**Why is a format listed, yet processing will not start?** The list shows an FFmpeg output found on this system. Encoder, container, and settings must pass a check together. The application message and Diagnostic Report identify the stage where the combination failed.

**Why will an MP4 file not play on a device?** The extension describes the container, which may hold different video and audio codecs, profiles, and channel layouts. Check both streams' properties and the device requirements.

**How do I obtain unchanged audio?** Select the audio track, leave **Original**, and extract it. A different codec requires conversion.

**Can I select several languages at once?** Yes. Select the desired audio or subtitle tracks. They become separate files, and you can enable language folders in settings. The application does not assemble them into a new combined movie.

**Why is the new file larger than the source?** A different codec, higher bitrate, uncompressed PCM, or lossless format can increase size. A larger file does not prove that source quality improved.

**Can I improve MP3 sound by converting it to FLAC?** No. FLAC will avoid new losses in the already decoded MP3, but cannot restore details discarded earlier.

**Why is CRF disabled, or why was no hardware encoder found?** CRF support is checked for the chosen encoder. Hardware search depends on FFmpeg, drivers, and hardware; another format or a software encoder may still be available.

**Can I get SRT from PGS, or one frame from GIF, merely by selecting a format?** There is no OCR for image-based subtitles. Extracting one still frame from an animated GIF is not a declared separate feature; do not confuse it with preserving the animation.

**Can GIF, APNG, or animated WebP be saved without re-encoding?** Recognized animated streams have original-copy routes. Short GIF creation from video was also verified manually; this does not confirm every animation scenario or input file.

**What if analysis succeeded but export failed?** First check the row status and log, then open the Diagnostic Report. A short test does not guarantee an entire real file is intact or that the selected container accepts every feature of it.

### 6.4. Application Limits and Feedback

Media Unseal handles **one source file at a time** and saves selected streams as separate outputs. It does not assemble them into a new combined movie. Its confirmed custom route for MKV attachments is limited to JPEG and PNG. Image-based subtitles do not become text: there is no OCR. A universal user-facing hardware-decoding mode and GPU filters are not implemented. Available outputs and encoders depend on the installed FFmpeg, drivers, and hardware.

The catalogs distinguish **confirmed application routes**, **dynamic conversion candidates**, and **capabilities listed only by FFmpeg**. An encoder's presence does not prove it works with every container or source. The examined local build produced a large output list after catalog parsing, but not all combinations were tested. Real files may contain damaged data, unusual metadata, or incompatible profiles. If a task fails, start with the stream status, log, and Diagnostic Report; check the output folder and FFmpeg/FFprobe readiness.

**Report a bug or suggest an improvement** at [sergjoyce@gmail.com](mailto:sergjoyce@gmail.com), also shown in application settings. Explain what you tried, the source and output formats, what you expected, and what happened. Use **Save log** to open the Diagnostic Report and, if helpful, save it to a text file for the message. Review the report before sending and remove information you do not want to share. The application does not send it automatically. If possible, attach a small file that reproduces the issue or provide exact steps.

The guide and application will be expanded as the project progresses and real cases are checked. This edition provides a substantive foundation for later refinements, diagrams, and translations. No list of future features or release dates is promised here.
