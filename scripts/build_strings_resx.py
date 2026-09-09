#!/usr/bin/env python3
"""Regenerate Resources/Strings.resx from the canonical key dictionary below.

This helper exists so the English base resource file can be maintained as
plain Python rather than hand-written XML.  The Tools/LocaleGenerator console
tool reads the produced Strings.resx at runtime to populate satellite locale
files via DeepL.

Run:
    python3 scripts/build_strings_resx.py
"""
from __future__ import annotations

import os
import xml.etree.ElementTree as ET
from typing import Dict

# ---------------------------------------------------------------------------
# English base strings.
#
# Keys follow descriptive Category_Name format so translators see context.
# Do NOT rename existing keys without also updating every AXAML reference and
# the satellite locale files under Resources/.
# ---------------------------------------------------------------------------
STRINGS: Dict[str, str] = {
    # ----- Shared / common -----
    "Common_Cancel": "Cancel",
    "Common_Close": "Close",
    "Common_Save": "Save",
    "Common_Clear": "Clear",
    "Common_Ok": "OK",
    "Common_Apply": "Apply",
    "Common_Browse": "Browse",
    "Common_Download": "Download",
    "Common_Restart": "Restart",

    # ----- Window chrome / title bar -----
    "Window_Title_Main": "Babel Player",
    "Window_Title_Settings": "Babel Player - Settings",
    "Window_Title_ApiKeys": "API Keys",
    "Window_Title_Crash": "Babel Player - Unhandled Error",
    "Window_Title_Wizard": "Speaker Reference Wizard",
    "Chrome_Minimize": "Minimize window",
    "Chrome_MaximizeRestore": "Maximize or restore window",
    "Chrome_Close": "Close window",

    # ----- Main window: file menu -----
    "Menu_File_Close": "Close",
    "Menu_File_ForceClose": "Force Close",

    # ----- Main window: pipeline panel -----
    "Section_Pipeline": "Pipeline",
    "Tooltip_ClearPipeline": "Clear pipeline and start fresh",
    "Section_TargetLanguage": "TARGET LANGUAGE",
    "Label_Output": "Output (dub / subs)",
    "Section_AudioAndDub": "AUDIO & DUB",
    "Label_SeparateVocals": "Separate vocals before transcription",
    "Section_Transcription": "TRANSCRIPTION",
    "Tooltip_RerunTranscription": "Re-run transcription (choose this stage only or include downstream)",
    "Automation_RerunTranscription": "Re-run transcription",
    "Label_Compute": "Compute",
    "Label_Provider": "Provider",
    "Label_Model": "Model",
    "Status_NeedsDownload": "⬇ Needs Download",
    "Status_Ready": "✓ Ready",
    "Label_SpokenLanguage": "Spoken language (ASR hint)",
    "SpokenLanguage_AutoDetect": "Auto-detect",
    "Section_Diarization": "DIARIZATION / SPEAKERS",
    "Tooltip_RerunDiarization": "Re-run speaker mapping (choose this stage only or include downstream)",
    "Automation_RerunDiarization": "Re-run speaker mapping",
    "Label_MultiSpeakerAudio": "Multi-speaker audio",
    "Hint_MultiSpeakerAudio": "Turn this on when the recording has more than one distinct speaker so lines can be labeled and dubbed separately. For a single narrator or one speaker, leave it off to avoid extra processing.",
    "Check_DetectSpeakers": "Detect and label separate speakers",
    "Label_DiarizationProvider": "Speaker detection engine",
    "Hint_DiarizationProvider": "WeSpeaker is the default. SortFormer is an on-device ONNX alternative for multi-speaker labeling.",
    "Hint_EnableMultiSpeakerForIds": "Enable multi-speaker detection above, then transcribe, to assign speaker IDs.",
    "Hint_SpeakerAssignmentsInWizard": "Speaker assignments are managed in the speaker setup wizard.",
    "Tooltip_OpenSpeakerWizard": "Opens the speaker setup wizard: per-speaker voice and (for Qwen) reference clips. Finish in the wizard saves changes.",
    "Button_SpeakerSetupWizard": "Speaker setup wizard…",
    "Section_Translation": "TRANSLATION",
    "Tooltip_RerunTranslation": "Re-run translation (choose this stage only or include dub)",
    "Automation_RerunTranslation": "Re-run translation",
    "Section_Dub": "DUB",
    "Tooltip_RerunDub": "Re-generate dub audio from the current translation",
    "Automation_RerunDub": "Re-generate dub audio",
    "Label_VoiceAssignment": "Voice assignment",
    "Check_AssignPerSpeakerInWizard": "Assign per speaker in Speaker Reference Wizard (fallback voice below)",
    "Hint_VoiceAssignment": "Use one voice for every speaker, or turn on the option above and set a fallback plus per-speaker voices in the wizard.",
    "Check_ChatterboxCloneConsent": "Consent to Chatterbox voice cloning",
    "Section_ApiKeys": "API KEYS",
    "Hint_ConfigureCredentials": "Configure credentials for the providers selected above.",
    "Button_ApiKeys": "API Keys",
    "Section_LanguageRouting": "LANGUAGE ROUTING",
    "Label_SourceAudio": "Source Audio",
    "Label_TargetSubDub": "Target Sub/Dub",
    "Section_ActiveConfig": "ACTIVE CONFIG",
    "Label_ActiveAsr": "ASR",
    "Label_ActiveCpu": "CPU",
    "Label_ActiveGpu": "GPU",
    "Label_ActiveNmt": "NMT",
    "Label_ActiveTts": "TTS",
    "Label_ActiveRam": "RAM",
    "Section_ProviderDiagnostics": "PROVIDER DIAGNOSTICS",
    "Status_Stale": "stale",

    # ----- Main window: run / cancel -----
    "Automation_PipelineProgress": "Pipeline progress",
    "Button_RunPipeline": "Run Pipeline",
    "Button_ContinuePipeline": "Continue",
    "Status_ReviewSpeakersThenContinue": "Speaker mapping ready. Review speakers, then continue.",
    "Status_RunningPipeline": "Running pipeline…",
    "Status_ContinuingPipeline": "Continuing pipeline…",
    "Button_CancelPipeline": "Cancel Pipeline",
    "Tooltip_ExpandErrorDetails": "Expand error details",
    "Automation_ExpandErrorDetails": "Expand error details",

    # ----- Main window: segments panel -----
    "Section_Segments": "Segments",
    "Label_SegmentCount_One": "{0} segment",
    "Label_SegmentCount_Many": "{0} segments",
    "Tooltip_ExportSegments": "Export captions (.srt), dubbed audio only (.mp3), or video with dub + soft subs (.mp4) — audio matches current timings and mix.",
    "Button_Export": "Export",
    "Label_SegmentCountSingle": "1 segment",
    "Label_SegmentCountFormat": "{0} segments",
    "Menu_Export_Srt": "to .srt",
    "Menu_Export_Mp3": "to .mp3",
    "Menu_Export_Mp4": "to .mp4",
    "Tooltip_RefreshSegments": "Refresh segments",
    "Automation_RefreshSegments": "Refresh segments",
    "Message_NoSegmentsTitle": "No segments yet",
    "Message_NoSegmentsHint": "Open media and run the pipeline to generate segments.",

    # ----- Main window: render timing -----
    "Label_RenderTiming": "Render timing",
    "Hint_RenderTiming": "Applies to generated dub audio and export.",
    "Label_SelectedSegment": "Selected segment",
    "Option_Inherit": "Inherit",
    "Tooltip_Inherit": "Use the session render timing for this segment",
    "Option_Off": "Off",
    "Tooltip_TimingOff": "Play TTS as-is for this segment in rendered dub/export",
    "Option_Stretch": "Stretch",
    "Tooltip_TimingStretch": "Time-stretch TTS to fit this segment window in rendered dub/export",
    # ----- Main window: playback controls -----
    "Tooltip_OpenMedia": "Open media file",
    "Automation_OpenMedia": "Open media",
    "Tooltip_ToggleSubtitles": "Toggle subtitles (C)",
    "Automation_ToggleSubtitles": "Toggle subtitles",
    "Tooltip_ToggleDubMode": "Toggle Dub Mode (D)",
    "Automation_ToggleDubMode": "Toggle Dub Mode",
    "Tooltip_SkipPrevious": "Skip to previous segment",
    "Automation_SkipPrevious": "Skip to previous segment",
    "Tooltip_Rewind10": "Rewind 10 seconds",
    "Automation_Rewind10": "Rewind 10 seconds",
    "Tooltip_PlayPause": "Play / Pause (Space)",
    "Automation_PlayPause": "Play or pause",
    "Tooltip_Forward10": "Fast forward 10 seconds",
    "Automation_Forward10": "Fast forward 10 seconds",
    "Tooltip_SkipNext": "Skip to next segment",
    "Automation_SkipNext": "Skip to next segment",
    "Tooltip_ToggleMute": "Toggle mute",
    "Automation_ToggleMute": "Toggle Mute",
    "Tooltip_Volume": "Volume",
    "Tooltip_PaneSideFormat": "{0} pane: {1} ({2})",
    "Automation_ToggleLeftPane": "Toggle left pane",
    "Automation_ToggleRightPane": "Toggle right pane",
    "Tooltip_ToggleFullscreen": "Toggle fullscreen (F11)",
    "Automation_ToggleFullscreen": "Toggle fullscreen",
    "Tooltip_OpenSettings": "Open settings",
    "Automation_Settings": "Settings",
    "Dev_Button_DevLog": "📋 Dev Log",
    "Dev_Tooltip_DevLog": "Open in-app dev log viewer",
    "Dev_Button_FreshStart": "🔄 Fresh Start",
    "Dev_Tooltip_FreshStart": "Cancel pipeline, wipe session temp files, reset UI",

    # ----- Settings window -----
    "Settings_Title": "Settings",
    "Settings_Nav_General": "General",
    "Settings_Nav_Hotkeys": "Hotkeys",
    "Settings_Nav_Video": "Video",
    "Settings_Nav_Models": "Models",
    "Settings_Nav_About": "About",
    "Settings_Nav_Diagnostics": "Diagnostics",
    "Settings_Group_Session": "Session",
    "Settings_Label_RecentLimit": "Recent session history limit",
    "Settings_Hint_RecentLimit": "Maximum number of recent sessions kept in the file menu (1\u201320).",
    "Settings_Check_AutoSave": "Auto-save session on exit",
    "Settings_Group_AppLanguage": "App language",
    "Settings_Hint_AppLanguage": "Changes the language of the Babel Player interface. Auto (system) follows your OS locale on each launch.",
    "Settings_Group_Layout": "Layout",
    "Settings_Check_ShowPipelinePane": "Show pipeline pane",
    "Settings_Check_ShowSegmentsPane": "Show segments pane",
    "Settings_Hint_PaneVisibility": "Use these toggles to restore a pane if it has been hidden from the main window.",
    "Settings_Check_SwapPaneSides": "Swap pane sides",
    "Settings_Hint_SwapPaneSides": "Moves the pipeline pane to the right and the segments pane to the left.",
    "Settings_Hint_PaneResize": "Double-click a pane divider to restore that pane to its default width.",
    "Settings_Option_AutoSystem": "Auto (system)",
    "Settings_Group_Gpu": "GPU Backend",
    "Settings_Label_PreferredGpu": "Preferred Local GPU Backend",
    "Settings_Hint_Gpu": "Managed local GPU is the default low-friction path. Docker GPU host is available as an advanced backend.",
    "Settings_Group_BackendStatus": "Backend Status",
    "Settings_Group_DockerGpu": "Docker GPU Service",
    "Settings_Label_AdvancedGpuUrl": "Advanced GPU Service URL",
    "Settings_Hint_AdvancedGpuUrl": "Only editable when the Docker GPU backend is selected.",
    "Settings_Check_AlwaysStartGpu": "Always start local GPU runtime at app start",
    "Settings_Group_PipelineAudio": "Pipeline audio & dubbing",
    "Settings_Hint_PipelineAudio": "Vocal separation and segment timing (dub) are configured in the main window → Pipeline panel, so they stay next to the run controls.",
    "Settings_Expander_AdvancedCpu": "Advanced: CPU Transcription",
    "Settings_Hint_AdvancedCpu": "These options tune CPU-only transcription. 0 threads means auto selection. Worker count can follow hardware (cores + RAM) or a manual value (clamped).",
    "Settings_Label_CpuComputeType": "CPU Compute Type:",
    "Settings_Label_CpuThreads": "CPU Threads (0=auto):",
    "Settings_Check_AutoWorkers": "Automatic worker count (from CPU cores and RAM)",
    "Settings_Label_WorkersManual": "Workers (manual):",
    "Settings_Group_Hotkeys": "Keyboard Shortcuts",
    "Settings_Hint_Hotkeys": "Hotkey customisation is not yet available. Shown values are the current defaults.",
    "Settings_Label_PlayPauseHotkey": "Play/Pause:",
    "Settings_Label_ToggleLeftPaneHotkey": "Toggle Left Pane:",
    "Settings_Label_ToggleRightPaneHotkey": "Toggle Right Pane:",
    "Settings_Label_ToggleDubHotkey": "Toggle Dub Mode:",
    "Settings_Label_ToggleFullscreenHotkey": "Toggle Fullscreen:",
    "Common_Left": "Left",
    "Common_Right": "Right",
    "Settings_Group_EmbeddedPlayback": "Embedded playback",
    "Settings_Check_BilingualSubs": "Bilingual subtitles (source + translation in exports and embedded preview)",
    "Settings_Hint_BilingualSubs": "When enabled, subtitle export and the in-player CC track include both languages when segments are available.",
    "Settings_Group_HardwareDecode": "Hardware Decode",
    "Settings_Label_HardwareDecoder": "Hardware Decoder:",
    "Settings_Label_GpuRenderApi": "GPU Rendering API:",
    "Settings_Label_ExportEncoder": "Export Encoder:",
    "Settings_Group_GpuNext": "GPU-Next Backend",
    "Settings_Check_UseGpuNext": "Use gpu-next video output (required for RTX enhancements)",
    "Settings_Hint_GpuNext": "gpu-next is the newer mpv renderer. Enable this before turning on RTX Video Super Resolution or HDR modes.",
    "Settings_Group_RtxVideo": "RTX Video Enhancement",
    "Settings_Check_Vsr": "RTX Video Super Resolution (VSR)",
    "Settings_Hint_Vsr": "AI upscaling via NVIDIA d3d11vpp. Requires a detected GeForce RTX GPU, driver \u2265 551.23, gpu-next, and 'RTX Video Enhancement' in NVIDIA Control Panel.",
    "Settings_Label_VsrSupport": "Support:",
    "Settings_Label_VsrRequested": "Requested:",
    "Settings_Label_VsrResolved": "Resolved:",
    "Settings_Label_VsrReason": "Last reason:",
    "Settings_Label_VsrFilter": "Last filter:",
    "Settings_Group_Hdr": "HDR Playback",
    "Settings_Hint_Hdr": "Choose one mode. RTX HDR follows NVIDIA Control Panel. HDR passthrough configures mpv. Requires gpu-next and Windows HDR.",
    "Settings_Radio_HdrOff": "Off",
    "Settings_Radio_HdrRtx": "RTX HDR",
    "Settings_Radio_HdrPassthrough": "HDR passthrough",
    "Settings_Label_MpvPassthrough": "mpv passthrough options",
    "Settings_Label_ToneMapping": "Tone mapping:",
    "Settings_Label_TargetPeak": "Target peak:",
    "Settings_Placeholder_TargetPeak": "auto or nits",
    "Settings_Label_HdrComputePeak": "HDR compute peak:",
    "Settings_Check_HdrComputePeak": "Dynamic per-frame peak (mpv)",
    "Settings_Hint_VideoRestart": "All video settings take effect after restarting the app.",
    "Settings_Group_Models": "Models",
    "Settings_Hint_Models": "Manage locally-hosted models. Cloud providers do not require downloads.",
    "Settings_About_AppName": "Babel Player",
    "Settings_About_License": "Apache-2.0 License",
    "Settings_About_Support": "Support the project:",
    "Settings_About_Kofi": "Ko-fi",
    "Settings_About_GitHubSponsors": "GitHub Sponsors",

    # ----- API keys dialog -----
    "Api_Title": "API Keys",
    "Api_Button_Save": "Save",
    "Api_Button_Clear": "Clear",
    "Api_Tooltip_ToggleReveal": "Show / hide key",
    "Api_Footer_StorageSecurity": "Storage Security:",
    "Api_Footer_MoreProviders": "More providers can be added in future updates.",

    # ----- Crash report window -----
    "Crash_Header": "An unhandled error occurred",
    "Crash_Subtitle": "The full error details are shown below. The error has also been written to the log file.",
    "Crash_Button_Copy": "Copy to Clipboard",
    "Crash_Button_OpenLogFolder": "Open Log Folder",
    "Crash_Button_Close": "Close",

    # ----- Speaker reference wizard -----
    "Wizard_Title": "Speaker Reference Wizard",
    "Wizard_Assign_Header": "Speakers",
    "Wizard_Assign_Hint": "Select a voice for each speaker. For Qwen, also set a reference audio clip from the video.",
    "Wizard_Preview_Header": "Preview",
    "Wizard_Preview_Hint": "Scrub through the video to find good audio clips for each speaker, then assign them below.",
    "Wizard_Preview_Unavailable": "Preview not available on this platform",
    "Wizard_Playhead_Clip_Tooltip": "Length of audio clip in seconds (3-15 seconds)",
    "Wizard_Button_Stop": "Stop",
    "Wizard_Label_Voice": "Voice",
    "Wizard_Label_VoiceHelp": "(for TTS generation)",
    "Wizard_Button_Auto": "Auto",
    "Wizard_Tooltip_Auto": "Use automatically detected voice for this speaker",
    "Wizard_Button_UseActive": "Use Active",
    "Wizard_Tooltip_UseActive": "Use the currently active TTS voice setting",
    "Wizard_Button_ClearVoice": "Clear",
    "Wizard_Tooltip_ClearVoice": "Clear the voice assignment, will use auto-detect",
    "Wizard_Placeholder_PiperVoice": "Or select a downloaded Piper voice...",
    "Wizard_Tooltip_PiperVoice": "Choose from downloaded Piper voices, or type a custom voice ID",
    "Wizard_Label_ReferenceClip": "Reference Clip",
    "Wizard_Label_ReferenceClipHelp": "(for voice cloning - Qwen only)",
    "Wizard_NoClipSelected": "No clip selected - Qwen voice cloning will use default",
    "Wizard_Tooltip_RefPath": "Audio clip used as reference for Qwen voice cloning",
    "Wizard_Button_Play": "Play",
    "Wizard_Button_Show": "Show",
    "Wizard_Button_Browse": "Browse",
    "Wizard_Button_UseSegment": "Use Segment",
    "Wizard_Tooltip_UseSegment": "Use the selected segment as reference (click a timestamp below first)",
    "Wizard_Button_UsePlayhead": "Use Playhead",
    "Wizard_Tooltip_UsePlayhead": "Grab the current playhead position from the preview - easiest way to set reference!",
    "Wizard_Button_AutoPick": "Auto-Pick",
    "Wizard_Tooltip_AutoPick": "Automatically find a good clip from this speaker's segments",
    "Wizard_Hint_JumpToSegments": "Jump to speaker's segments",
    "Wizard_Tooltip_JumpToSegments": "Click a timestamp to jump to that point in the video, then use 'Use Playhead'",
    "Wizard_Tooltip_ConfidenceReason": "Why this clip was rated as good/poor",
    "Wizard_Tooltip_ConfidenceTier": "Quality of the reference clip for this speaker",
    "Wizard_Tooltip_LastAction": "Last action taken on this speaker",
    "Wizard_Expander_Advanced": "Advanced",
    "Wizard_Label_MergeSpeakers": "Merge speakers",
    "Wizard_Tooltip_MergeSource": "Select source speaker to merge",
    "Wizard_Tooltip_MergeTarget": "Select target speaker (will receive the merged segments)",
    "Wizard_Button_Merge": "Merge",
    "Wizard_Tooltip_Merge": "Merge source into target - this updates the transcript permanently",
    "Wizard_Footer_Hint": "Finish: save changes. Cancel: discard edits.",
    "Wizard_Button_Cancel": "Cancel",
    "Wizard_Tooltip_Cancel": "Discard edits",
    "Wizard_Button_Finish": "Finish",
    "Wizard_Tooltip_Finish": "Save changes",
    "Wizard_Tooltip_Close": "Close wizard",
    "Wizard_Tooltip_CloseDiscard": "Discard changes",
    "Wizard_Hint_TopBanner": "Assign voices and set reference clips.",
    "Wizard_Toggle_NeedsAttention": "Show needs attention",
    "Wizard_Tooltip_NeedsAttention": "Show speakers needing review",
    "Wizard_Button_ResetAll": "Reset All",
    "Wizard_Tooltip_ResetAll": "Clear all changes",
    "Wizard_Hint_ScrubPreview": "Scrub to find clips; click Use Playhead.",
    "Wizard_Hint_LinuxMacFallback": "Scrub in main window, return here, click Use Playhead.",
    "Wizard_Tooltip_SeekSlider": "Drag to seek through the video",
    "Wizard_Tooltip_PlayPause": "Play or pause the preview",
    "Wizard_Label_ClipDuration": "Playhead Clip Duration",
    "Wizard_Tooltip_ClipDuration": "How much audio to grab before and after the playhead position",

    # ----- Diagnostics panel -----
    "Diag_HardwareSnapshot": "Hardware Snapshot",
    "Diag_Cpu": "CPU:",
    "Diag_Gpu": "GPU:",
    "Diag_Ram": "System RAM:",
    "Diag_RuntimeRouting": "Runtime Routing",
    "Diag_InferenceHost": "Inference Host:",
    "Diag_WarmupStatus": "Warmup Status:",
    "Diag_NmtFallback": "NMT Fallback:",
    "Diag_GpuProbeStreak": "GPU probe streak:",
    "Diag_Environment": "Environment",
    "Diag_PythonPath": "Python Path:",
    "Diag_FfmpegPath": "ffmpeg Path:",
    "Diag_Button_Refresh": "Refresh Diagnostics",
    "Settings_Button_SaveClose": "Save & Close",

    # ----- Language display names (English base) -----
    # Localized files translate each value into the target language.
    "Language_ar": "Arabic",
    "Language_de": "German",
    "Language_en": "English",
    "Language_es": "Spanish",
    "Language_fr": "French",
    "Language_hi": "Hindi",
    "Language_it": "Italian",
    "Language_ja": "Japanese",
    "Language_ko": "Korean",
    "Language_nl": "Dutch",
    "Language_pl": "Polish",
    "Language_pt": "Portuguese",
    "Language_ru": "Russian",
    "Language_sv": "Swedish",
    "Language_tr": "Turkish",
    "Language_zh": "Chinese (Simplified)",
}

RESX_HEADER = '''<?xml version="1.0" encoding="utf-8"?>
<root>
  <!--
    Generated by scripts/build_strings_resx.py.
    Edit the Python dictionary there and rerun the script; do not hand-edit this XML.
  -->
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:import namespace="http://www.w3.org/XML/1998/namespace" />
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="metadata">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" />
              </xsd:sequence>
              <xsd:attribute name="name" use="required" type="xsd:string" />
              <xsd:attribute name="type" type="xsd:string" />
              <xsd:attribute name="mimetype" type="xsd:string" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="assembly">
            <xsd:complexType>
              <xsd:attribute name="alias" type="xsd:string" />
              <xsd:attribute name="name" type="xsd:string" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" msdata:Ordinal="1" />
              <xsd:attribute name="type" type="xsd:string" msdata:Ordinal="3" />
              <xsd:attribute name="mimetype" type="xsd:string" msdata:Ordinal="4" />
              <xsd:attribute ref="xml:space" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype">
    <value>text/microsoft-resx</value>
  </resheader>
  <resheader name="version">
    <value>2.0</value>
  </resheader>
  <resheader name="reader">
    <value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
  <resheader name="writer">
    <value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value>
  </resheader>
'''


def _escape(text: str) -> str:
    return (text
            .replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;"))


def write_resx(path: str, entries: Dict[str, str]) -> None:
    lines = [RESX_HEADER]
    for key in entries:
        value = entries[key]
        lines.append(f'  <data name="{_escape(key)}" xml:space="preserve">')
        lines.append(f'    <value>{_escape(value)}</value>')
        lines.append('  </data>')
    lines.append('</root>')
    lines.append('')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines))


def main() -> None:
    repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_path = os.path.join(repo_root, "Resources", "Strings.resx")
    write_resx(out_path, STRINGS)
    print(f"Wrote {len(STRINGS)} keys to {out_path}")


if __name__ == "__main__":
    main()
