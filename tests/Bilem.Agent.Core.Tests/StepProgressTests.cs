using System.Linq;
using Bilem.Agent.Core;
using Xunit;

namespace Bilem.Agent.Core.Tests
{
    /// <summary>Окно «Настройка базы»: «Шаг N из M · P%» и полоса (stand #75, 10.11).</summary>
    public class StepProgressTests
    {
        private static Step[] Steps(params StepStatus[] st) => st.Select((s, i) => new Step { Id = "s" + i, Status = s }).ToArray();

        [Fact]
        public void Running_CountsOkWarnSkippedAsDone()
        {
            Assert.Equal(9, Onboarding.SetupSteps.Length);
            // Четыре сделаны (пропущенный и «внимание» — тоже), идёт пятый
            var p = StepProgress.Of(Steps(StepStatus.Ok, StepStatus.Ok, StepStatus.Warn, StepStatus.Skipped, StepStatus.Running), 9);
            Assert.Equal(4, p.Done);
            Assert.Equal(5, p.Current);
            Assert.Equal(44, p.Percent);
            Assert.False(p.Failed);
            Assert.Equal(Txt.F("UiProgress", 5, 9, 44), p.Text());
            // Ещё ни одного шага
            Assert.Equal(1, StepProgress.Of(Steps(), 9).Current);
        }

        [Fact]
        public void Fail_ShowsFailedStepNumber()
        {
            var p = StepProgress.Of(Steps(StepStatus.Ok, StepStatus.Ok, StepStatus.Fail), 9, finished: false);
            Assert.True(p.Failed);
            Assert.Equal(3, p.Current);
            Assert.Equal(Txt.F("UiProgressFail", 3, 9), p.Text());
        }

        [Fact]
        public void Done_IsHundredPercent_FallbackAddsStep()
        {
            var nine = Enumerable.Repeat(StepStatus.Ok, 9).ToArray();
            var p = StepProgress.Of(Steps(nine), 9, finished: true);
            Assert.Equal(100, p.Percent);
            Assert.Equal(Txt.F("UiProgressDone", 9, 9), p.Text());
            // Откат на Apache — десятый шаг: в плане 10, не «10 из 9»
            var ten = StepProgress.Of(Steps(Enumerable.Repeat(StepStatus.Ok, 9).Concat(new[] { StepStatus.Running }).ToArray()), 10);
            Assert.Equal(10, ten.Current);
            Assert.Equal(10, ten.Total);
            Assert.Equal(90, ten.Percent);
        }
    }
}
