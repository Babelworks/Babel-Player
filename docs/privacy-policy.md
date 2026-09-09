# Privacy Policy

**Effective Date:** March 26, 2026  
**Last updated:** September 9, 2026

## 1. Local-First Design
Babel Player is designed as a **local-first** desktop application.
*   **On-device by default:** Video processing, transcription (STT), and voice synthesis (TTS) can run entirely on your device (GPU/NPU/CPU) when you use local providers.
*   **No User Accounts:** We do not require sign-ups, emails, or passwords.
*   **No "Improvement" Data:** We do not collect your usage data to train our models.

## 2. Network Activity
The application will only access the internet for the following explicit user-initiated actions:
*   **Model Downloads:** When you explicitly click to download a voice model or optimization pack (e.g., from GitHub or Hugging Face).
*   **Updates:** To check for new versions of the player (can be disabled in Settings).
*   **Optional cloud providers:** Only when you enable a cloud API provider and supply your own key (see section 5).

## 3. Telemetry & Analytics
*   **Application Telemetry:** Basic crash reporting may be active via the .NET runtime/Avalonia framework. We are actively fundraising to purchase a commercial license to disable this completely.
*   **User Tracking:** We use **Zero** marketing trackers, pixels, or cookies.

## 4. Persistence
*   **Local app data:** Settings, speaker mappings, and related app state are stored under `%LOCALAPPDATA%\BabelPlayer\` on Windows. This data does not leave your machine unless you use an optional cloud provider.
*   **Session / media artifacts:** Transcripts, translations, and dub artifacts are stored in session/project folders you choose or under local app data. They are not uploaded by Babel Player itself.

## 5. User-Directed Cloud Inference (Optional)
Babel Player offers optional features that allow you to use third-party Cloud APIs (e.g., ElevenLabs, OpenAI) for higher-fidelity translation or voice synthesis.

*   **Bring Your Own Key (BYOK):** These features ONLY function if you explicitly provide your own personal API Key in the settings.
*   **Direct Connection:** When active, Babel Player establishes a direct encrypted connection between your device and the third-party provider. **Your data does not pass through Babel Player's servers.**
*   **Third-Party Privacy:** By using these features, you acknowledge that your text/audio data is subject to the privacy policy of the provider you have chosen (e.g., OpenAI's data retention policy).
*   **No Key Logging:** Your API keys are stored locally on your device using OS-level encryption (Windows DPAPI). We do not sync or collect your keys.
