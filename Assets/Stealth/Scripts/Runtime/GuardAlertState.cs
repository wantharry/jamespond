namespace Blocks.Gameplay.Stealth
{
    /// <summary>
    /// The awareness states a guard moves through. Ordered by escalation, so states can be
    /// compared relationally (e.g. <c>state >= GuardAlertState.Investigating</c> to test
    /// "has noticed something").
    /// </summary>
    public enum GuardAlertState : byte
    {
        /// <summary>
        /// Default behaviour. The guard walks its patrol route and has seen nothing.
        /// </summary>
        Patrolling = 0,

        /// <summary>
        /// Something entered vision but awareness has not filled yet. The guard stops and
        /// turns to look, but does not leave its route. Awareness decays back to
        /// <see cref="Patrolling"/> if the target breaks line of sight.
        /// </summary>
        Suspicious = 1,

        /// <summary>
        /// Awareness filled, then the target was lost. The guard walks to the last known
        /// position and searches nearby before giving up.
        /// </summary>
        Investigating = 2,

        /// <summary>
        /// Awareness is full and the target is visible. The guard actively pursues.
        /// </summary>
        Alerted = 3
    }
}
