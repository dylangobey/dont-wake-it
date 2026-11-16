namespace DontWakeIt.Items
{
    public interface IInteractable
    {
        string Prompt { get; }

        bool CanInteract(PlayerInventory who);

        void Interact(PlayerInventory who);
    }
}