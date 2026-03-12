using UnityEngine;
using TMPro;

public class RuptureWarningUI : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI label;
    [SerializeField] private float fadeDelay = 3f;

    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip alertSfx;

    private HiveOvermind overmind;
    private bool subscribed = false;

    private float countdown = -1f;
    private int incomingCount = 0;

    private void OnEnable()
    {
        TrySubscribe();
    }

    private void Update()
    {
        // Overmind may spawn later
        if (!subscribed)
            TrySubscribe();

        if (countdown > 0f)
        {
            countdown -= Time.deltaTime;
            UpdateLabel();

            if (countdown <= 0f)
            {
                countdown = -1f;
                Invoke(nameof(HideLabel), fadeDelay);
            }
        }
    }

    private void OnDisable()
    {
        if (subscribed && overmind != null)
            overmind.OnRuptureWaveScheduled -= HandleWaveScheduled;

        subscribed = false;
        overmind = null;
    }

    private void TrySubscribe()
    {
        if (subscribed) return;

        overmind = HiveOvermind.Instance;
        if (!overmind) return;

        overmind.OnRuptureWaveScheduled += HandleWaveScheduled;
        subscribed = true;

        Debug.Log("[UI] Subscribed to rupture warnings.");
    }

    private void HandleWaveScheduled(int count, float eta)
    {
        incomingCount = count;
        countdown = eta;

        // Play SFX once when warning starts
        if (audioSource && alertSfx)
            audioSource.PlayOneShot(alertSfx);

        UpdateLabel();
        if (label) label.gameObject.SetActive(true);
    }

    private void UpdateLabel()
    {
        if (!label) return;

        float t = Mathf.Max(0f, countdown);
        string tStr = t.ToString("00.00"); // 30.00 style

        label.text =
            "<b>⚠ WARNING: Rupture Activity Detected</b>\n" +
            $"Sites: {incomingCount}\n" +
            $"Arrival In: {tStr}s";
    }

    private void HideLabel()
    {
        if (label)
            label.gameObject.SetActive(false);
    }
}