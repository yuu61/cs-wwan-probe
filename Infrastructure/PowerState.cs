using Windows.System.Power;

namespace WwanProbe.Infrastructure
{
    // Whether Windows is saving power: on battery power, or with Energy Saver (Battery Saver) on.
    internal interface IPowerState
    {
        bool IsSaving { get; }
    }

    // WinRT PowerManager, read on demand: the monitor asks once as each sample completes, so no events
    // are subscribed. A failed read counts as not saving.
    internal sealed class WinRtPowerState : IPowerState
    {
        public bool IsSaving
        {
            get
            {
                try { return PowerManager.PowerSupplyStatus == PowerSupplyStatus.NotPresent || PowerManager.EnergySaverStatus == EnergySaverStatus.On; }
                catch (Exception) { return false; }
            }
        }
    }
}
