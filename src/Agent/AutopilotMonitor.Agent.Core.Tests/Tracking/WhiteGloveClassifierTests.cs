using AutopilotMonitor.Agent.Core.Monitoring.Enrollment.Completion;
using Xunit;

namespace AutopilotMonitor.Agent.Core.Tests.Tracking
{
    public class WhiteGloveClassifierTests
    {
        // =====================================================================
        // Zero / None baseline
        // =====================================================================

        [Fact]
        public void Classify_AllSignalsOff_ReturnsNone()
        {
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals());

            Assert.Equal(WhiteGloveConfidence.None, result.Confidence);
            Assert.Equal(0, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
            Assert.Empty(result.ContributingFactors);
        }

        // =====================================================================
        // Single-signal behaviour (the core bugfix)
        // =====================================================================

        [Fact]
        public void Classify_ShellCoreAlone_IsStrong()
        {
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
            });

            Assert.Equal(WhiteGloveConfidence.Strong, result.Confidence);
            Assert.Equal(80, result.Score);
            Assert.True(result.ShouldRouteToWhiteGlovePart1);
        }

        [Fact]
        public void Classify_OnlySaveWgSuccessResult_IsNoneNotWg()
        {
            // The core bug-avoidance test: SaveWhiteGloveSuccessResult=true alone has been
            // observed on genuinely non-WG devices and MUST NOT classify as WhiteGlove.
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                HasSaveWhiteGloveSuccessResult = true,
            });

            Assert.Equal(WhiteGloveConfidence.None, result.Confidence);
            Assert.Equal(10, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
        }

        [Fact]
        public void Classify_SaveWgPlusDeviceOnly_IsStillNone()
        {
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                HasSaveWhiteGloveSuccessResult = true,
                IsDeviceOnlyDeployment = true,
            });

            Assert.Equal(WhiteGloveConfidence.None, result.Confidence);
            Assert.Equal(25, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
        }

        [Fact]
        public void Classify_WeakSignalVerbundJustBelowThreshold_IsWeakNotWg()
        {
            // Mehrere weiche Signale alleine reichen nicht fuer Strong.
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                IsFooUserDetected = true,           // +20
                IsWhiteGloveStartDetected = true,   // +15
                IsDeviceOnlyDeployment = true,      // +15
            });

            Assert.Equal(WhiteGloveConfidence.Weak, result.Confidence);
            Assert.Equal(50, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
        }

        [Fact]
        public void Classify_WeakSignalVerbundAtThreshold_IsStrongAndRoutes()
        {
            // Signalverbund erreicht gerade die 70 — laut Plan: WG.
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                HasSaveWhiteGloveSuccessResult = true,   // +10
                IsFooUserDetected = true,                // +20
                IsWhiteGloveStartDetected = true,        // +15
                IsDeviceOnlyDeployment = true,           // +15
                AgentRestartedAfterEspExit = true,       // +10
            });

            Assert.Equal(WhiteGloveConfidence.Strong, result.Confidence);
            Assert.Equal(70, result.Score);
            Assert.True(result.ShouldRouteToWhiteGlovePart1);
        }

        // =====================================================================
        // Hard excluders
        // =====================================================================

        [Fact]
        public void Classify_AadJoinedWithUser_OverridesStrongPositive()
        {
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
                AadJoinedWithUser = true, // real user — definitively not WG Part 1
            });

            Assert.Equal(WhiteGloveConfidence.None, result.Confidence);
            Assert.Equal(0, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
        }

        [Fact]
        public void Classify_DesktopArrived_OverridesStrongPositive()
        {
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
                DesktopArrived = true,
            });

            Assert.Equal(WhiteGloveConfidence.None, result.Confidence);
            Assert.Equal(0, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
        }

        [Fact]
        public void Classify_AccountSetupActivity_DampensButDoesNotHardExclude()
        {
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,      // +80
                HasAccountSetupActivity = true,         // -40
            });

            // 80 - 40 = 40 → Weak (NOT routed to WG)
            Assert.Equal(WhiteGloveConfidence.Weak, result.Confidence);
            Assert.Equal(40, result.Score);
            Assert.False(result.ShouldRouteToWhiteGlovePart1);
        }

        // =====================================================================
        // Placeholder user (foouser@ / autopilot@) — NOT aadJoinedWithUser
        // =====================================================================

        [Fact]
        public void Classify_FooUserWithoutRealAadJoin_StaysStrong()
        {
            // Placeholder-User ist KEIN realer AAD-Join; Hard-Excluder greift nicht.
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,  // +80
                IsFooUserDetected = true,           // +20
                AadJoinedWithUser = false,          // Placeholder zählt NICHT als real
            });

            Assert.Equal(WhiteGloveConfidence.Strong, result.Confidence);
            Assert.Equal(100, result.Score); // capped
            Assert.True(result.ShouldRouteToWhiteGlovePart1);
        }

        // =====================================================================
        // Hybrid join is NOT a dampener (removed per plan review)
        // =====================================================================

        [Fact]
        public void Classify_HybridWithStrongSignal_StaysStrong()
        {
            // Hybrid-Join darf die WG-Klassifikation nicht dämpfen (WG funktioniert auch hybrid).
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
                IsDeviceOnlyDeployment = true,
            });

            Assert.Equal(WhiteGloveConfidence.Strong, result.Confidence);
            Assert.Equal(95, result.Score);
            Assert.True(result.ShouldRouteToWhiteGlovePart1);
        }

        // =====================================================================
        // Late AAD-Join Transition
        // =====================================================================

        [Fact]
        public void Classify_LateAadJoinFlipsStrongToNone()
        {
            // Vor dem Late-Update: Strong.
            var before = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
                AadJoinedWithUser = false,
            });
            Assert.Equal(WhiteGloveConfidence.Strong, before.Confidence);
            Assert.True(before.ShouldRouteToWhiteGlovePart1);

            // Nach dem Late-AAD-Update: None (harter Ausschluss).
            var after = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
                AadJoinedWithUser = true,
            });
            Assert.Equal(WhiteGloveConfidence.None, after.Confidence);
            Assert.False(after.ShouldRouteToWhiteGlovePart1);
        }

        // =====================================================================
        // Session 304620a8 regression — WhiteGlove Part 2 re-entry after resume
        // =====================================================================

        [Fact]
        public void Classify_Part2ResumeWithAllPositivesAndAadJoin_IsWeakNotStrong()
        {
            // Reproduziert den Signalstand beim 2. ESP-Exit in Session 304620a8-...:
            // Part 1 ist sauber durchgelaufen (Shell-Core + SaveWg + Event509 + FooUser),
            // Agent hat nach Part 1 rebootet, in Part 2 hat der reale User sich
            // eingeloggt (AadJoinedWithUser=true). Der 2. ESP-Exit darf nicht erneut
            // zu whiteglove_complete routen — Hard-Excluder muss das Routing kippen.
            var result = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,      // +80
                HasSaveWhiteGloveSuccessResult = true,  // +10
                IsWhiteGloveStartDetected = true,       // +15
                IsFooUserDetected = true,               // +20
                AgentRestartedAfterEspExit = true,      // +10
                AadJoinedWithUser = true,               // -100 hard excluder
            });

            Assert.False(result.ShouldRouteToWhiteGlovePart1);
            Assert.NotEqual(WhiteGloveConfidence.Strong, result.Confidence);
        }

        // =====================================================================
        // ShouldRouteToWhiteGlovePart1 only fires on Strong
        // =====================================================================

        [Fact]
        public void Classify_ShouldRoute_OnlyOnStrong()
        {
            var none = WhiteGloveClassifier.Classify(new WhiteGloveSignals());
            var weak = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                IsFooUserDetected = true,
                IsDeviceOnlyDeployment = true,
            });
            var strong = WhiteGloveClassifier.Classify(new WhiteGloveSignals
            {
                ShellCoreWhiteGloveSuccess = true,
            });

            Assert.Equal(WhiteGloveConfidence.None, none.Confidence);
            Assert.False(none.ShouldRouteToWhiteGlovePart1);

            Assert.Equal(WhiteGloveConfidence.Weak, weak.Confidence);
            Assert.False(weak.ShouldRouteToWhiteGlovePart1);

            Assert.Equal(WhiteGloveConfidence.Strong, strong.Confidence);
            Assert.True(strong.ShouldRouteToWhiteGlovePart1);
        }
    }
}
