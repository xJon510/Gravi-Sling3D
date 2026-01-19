using UnityEngine;

[RequireComponent(typeof(Collider))]
public class QuestArrivedTrigger : MonoBehaviour
{
    private PlanetQuestNavigator _quest;
    private int _planetId;
    private string _requiredTag;

    public void Init(PlanetQuestNavigator quest, int planetId, string requiredTag)
    {
        _quest = quest;
        _planetId = planetId;
        _requiredTag = requiredTag ?? "";

        var col = GetComponent<Collider>();
        col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (_quest == null) return;

        if (!string.IsNullOrEmpty(_requiredTag) && !other.CompareTag(_requiredTag))
            return;

        // Disarm this trigger so it doesn't spam (you can re-arm per quest)
        gameObject.SetActive(false);

        _quest.NotifyArrived(_planetId);
    }
}
