using EntityStates;

namespace MarioRoR2
{
    /// <summary>
    /// Mario's body state. Commando's main state would move the body and fire skills from the same inputs SM64
    /// reads; this one leaves both to MarioController.
    /// </summary>
    public class MarioMain : BaseState
    {
        public override InterruptPriority GetMinimumInterruptPriority() => InterruptPriority.Any;
    }
}
