using Brutality.Audio;

namespace Brutality.Tests.Audio;

public class DeviceRecoveryTests
{
    const string Controller = "DDJ-SB3";
    const string SecondCard = "USB Audio";

    [Fact]
    public void All_well_on_the_chosen_device_needs_nothing()
    {
        Assert.Equal(OutputStep.None, DeviceRecovery.ForOutput(Controller, open: true, current: Controller, stalled: false, chosenPresent: true, mayRetry: true));
        Assert.Equal(OutputStep.None, DeviceRecovery.ForOutput(null, open: true, current: null, stalled: false, chosenPresent: true, mayRetry: true));
    }

    [Fact]
    public void A_chosen_device_unplugged_leaves_the_mix_on_the_default()
    {
        Assert.Equal(OutputStep.FallBack, DeviceRecovery.ForOutput(Controller, open: true, current: Controller, stalled: false, chosenPresent: false, mayRetry: true));
    }

    [Fact]
    public void A_chosen_device_that_stops_asking_for_audio_counts_as_lost_even_if_listed()
    {
        Assert.Equal(OutputStep.FallBack, DeviceRecovery.ForOutput(Controller, open: true, current: Controller, stalled: true, chosenPresent: true, mayRetry: true));
    }

    [Fact]
    public void Back_on_the_chosen_device_as_soon_as_it_is_there_again()
    {
        Assert.Equal(OutputStep.Return, DeviceRecovery.ForOutput(Controller, open: true, current: null, stalled: false, chosenPresent: true, mayRetry: true));
    }

    [Fact]
    public void A_device_that_just_went_or_would_not_open_is_left_a_while_first()
    {
        Assert.Equal(OutputStep.None, DeviceRecovery.ForOutput(Controller, open: true, current: null, stalled: false, chosenPresent: true, mayRetry: false));
    }

    [Fact]
    public void Waiting_for_the_chosen_device_the_default_is_kept_going()
    {
        Assert.Equal(OutputStep.None, DeviceRecovery.ForOutput(Controller, open: true, current: null, stalled: false, chosenPresent: false, mayRetry: true));
        Assert.Equal(OutputStep.ReopenDefault, DeviceRecovery.ForOutput(Controller, open: true, current: null, stalled: true, chosenPresent: false, mayRetry: true));
    }

    [Fact]
    public void The_default_stopping_is_opened_afresh()
    {
        Assert.Equal(OutputStep.ReopenDefault, DeviceRecovery.ForOutput(null, open: true, current: null, stalled: true, chosenPresent: true, mayRetry: true));
    }

    [Fact]
    public void With_nothing_open_the_chosen_device_or_else_the_default_is_opened()
    {
        Assert.Equal(OutputStep.Return, DeviceRecovery.ForOutput(Controller, open: false, current: null, stalled: false, chosenPresent: true, mayRetry: true));
        Assert.Equal(OutputStep.ReopenDefault, DeviceRecovery.ForOutput(Controller, open: false, current: null, stalled: false, chosenPresent: false, mayRetry: true));
        Assert.Equal(OutputStep.ReopenDefault, DeviceRecovery.ForOutput(Controller, open: false, current: null, stalled: false, chosenPresent: true, mayRetry: false));
        Assert.Equal(OutputStep.ReopenDefault, DeviceRecovery.ForOutput(null, open: false, current: null, stalled: false, chosenPresent: true, mayRetry: true));
    }

    [Fact]
    public void Headphones_on_their_own_card_go_with_it_and_come_back_with_it()
    {
        Assert.Equal(HeadphoneStep.None, DeviceRecovery.ForHeadphones(SecondCard, Controller, current: SecondCard, chosenPresent: true, mayRetry: true));
        Assert.Equal(HeadphoneStep.Drop, DeviceRecovery.ForHeadphones(SecondCard, Controller, current: SecondCard, chosenPresent: false, mayRetry: true));
        Assert.Equal(HeadphoneStep.None, DeviceRecovery.ForHeadphones(SecondCard, Controller, current: null, chosenPresent: false, mayRetry: true));
        Assert.Equal(HeadphoneStep.Return, DeviceRecovery.ForHeadphones(SecondCard, Controller, current: null, chosenPresent: true, mayRetry: true));
        Assert.Equal(HeadphoneStep.None, DeviceRecovery.ForHeadphones(SecondCard, Controller, current: null, chosenPresent: true, mayRetry: false));
    }

    [Fact]
    public void Headphones_on_the_outputs_channels_3_and_4_are_left_to_the_output()
    {
        Assert.Equal(HeadphoneStep.None, DeviceRecovery.ForHeadphones(Controller, Controller, current: null, chosenPresent: false, mayRetry: true));
        Assert.Equal(HeadphoneStep.None, DeviceRecovery.ForHeadphones(Controller, Controller, current: null, chosenPresent: true, mayRetry: true));
    }

    [Fact]
    public void No_headphones_chosen_needs_nothing()
    {
        Assert.Equal(HeadphoneStep.None, DeviceRecovery.ForHeadphones(null, Controller, current: null, chosenPresent: false, mayRetry: true));
    }
}
