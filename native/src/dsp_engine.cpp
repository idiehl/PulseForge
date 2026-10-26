#include "dsp_engine.h"

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <numbers>
#include <new>

namespace pulseforge {
namespace {

constexpr std::size_t ChannelCount = 2;
constexpr std::size_t MaximumFilterCount = PF_MAX_FILTERS;
constexpr std::size_t MaximumPassStages = 4;
constexpr std::size_t DampingBandCount = 24;
constexpr std::size_t DampingNotchCount = 4;
constexpr std::size_t MaximumDelaySamples = 3'842;
constexpr std::size_t LimiterDelaySamples = 970;
constexpr std::uint32_t CrossfadeFrames = 128;
constexpr double Epsilon = 1.0e-12;

[[nodiscard]] double clampFinite(double value, double minimum, double maximum, double fallback) noexcept
{
    return std::isfinite(value) ? std::clamp(value, minimum, maximum) : fallback;
}

[[nodiscard]] double dbToLinear(double value) noexcept
{
    return std::pow(10.0, value / 20.0);
}

[[nodiscard]] float linearToDb(double value) noexcept
{
    return static_cast<float>(20.0 * std::log10(std::max(value, 1.0e-6)));
}

struct Biquad final {
    double b0 = 1.0;
    double b1 = 0.0;
    double b2 = 0.0;
    double a1 = 0.0;
    double a2 = 0.0;
    double z1 = 0.0;
    double z2 = 0.0;

    void reset() noexcept
    {
        z1 = 0.0;
        z2 = 0.0;
    }

    [[nodiscard]] double process(double input) noexcept
    {
        const double output = b0 * input + z1;
        z1 = b1 * input - a1 * output + z2;
        z2 = b2 * input - a2 * output;
        return std::isfinite(output) ? output : 0.0;
    }

    void setNormalized(double numerator0, double numerator1, double numerator2, double denominator0, double denominator1, double denominator2) noexcept
    {
        const double inverseA0 = 1.0 / denominator0;
        b0 = numerator0 * inverseA0;
        b1 = numerator1 * inverseA0;
        b2 = numerator2 * inverseA0;
        a1 = denominator1 * inverseA0;
        a2 = denominator2 * inverseA0;
    }

    void setLowPass(double frequency, double q, double sampleRate) noexcept
    {
        const double omega = 2.0 * std::numbers::pi * frequency / sampleRate;
        const double cosine = std::cos(omega);
        const double alpha = std::sin(omega) / (2.0 * q);
        setNormalized((1.0 - cosine) * 0.5, 1.0 - cosine, (1.0 - cosine) * 0.5, 1.0 + alpha, -2.0 * cosine, 1.0 - alpha);
    }

    void setHighPass(double frequency, double q, double sampleRate) noexcept
    {
        const double omega = 2.0 * std::numbers::pi * frequency / sampleRate;
        const double cosine = std::cos(omega);
        const double alpha = std::sin(omega) / (2.0 * q);
        setNormalized((1.0 + cosine) * 0.5, -(1.0 + cosine), (1.0 + cosine) * 0.5, 1.0 + alpha, -2.0 * cosine, 1.0 - alpha);
    }

    void setBandPass(double frequency, double q, double sampleRate) noexcept
    {
        const double omega = 2.0 * std::numbers::pi * frequency / sampleRate;
        const double alpha = std::sin(omega) / (2.0 * q);
        const double cosine = std::cos(omega);
        setNormalized(alpha, 0.0, -alpha, 1.0 + alpha, -2.0 * cosine, 1.0 - alpha);
    }

    void setNotch(double frequency, double q, double sampleRate) noexcept
    {
        const double omega = 2.0 * std::numbers::pi * frequency / sampleRate;
        const double alpha = std::sin(omega) / (2.0 * q);
        const double cosine = std::cos(omega);
        setNormalized(1.0, -2.0 * cosine, 1.0, 1.0 + alpha, -2.0 * cosine, 1.0 - alpha);
    }

    void setBell(double frequency, double gainDb, double q, double sampleRate) noexcept
    {
        const double amplitude = std::pow(10.0, gainDb / 40.0);
        const double omega = 2.0 * std::numbers::pi * frequency / sampleRate;
        const double alpha = std::sin(omega) / (2.0 * q);
        const double cosine = std::cos(omega);
        setNormalized(
            1.0 + alpha * amplitude,
            -2.0 * cosine,
            1.0 - alpha * amplitude,
            1.0 + alpha / amplitude,
            -2.0 * cosine,
            1.0 - alpha / amplitude);
    }

    void setShelf(double frequency, double gainDb, bool highShelf, double sampleRate) noexcept
    {
        const double amplitude = std::pow(10.0, gainDb / 40.0);
        const double omega = 2.0 * std::numbers::pi * frequency / sampleRate;
        const double cosine = std::cos(omega);
        const double sine = std::sin(omega);
        const double alpha = sine * std::sqrt(2.0) * 0.5;
        const double root = 2.0 * std::sqrt(amplitude) * alpha;
        if (highShelf) {
            setNormalized(
                amplitude * ((amplitude + 1.0) + (amplitude - 1.0) * cosine + root),
                -2.0 * amplitude * ((amplitude - 1.0) + (amplitude + 1.0) * cosine),
                amplitude * ((amplitude + 1.0) + (amplitude - 1.0) * cosine - root),
                (amplitude + 1.0) - (amplitude - 1.0) * cosine + root,
                2.0 * ((amplitude - 1.0) - (amplitude + 1.0) * cosine),
                (amplitude + 1.0) - (amplitude - 1.0) * cosine - root);
        } else {
            setNormalized(
                amplitude * ((amplitude + 1.0) - (amplitude - 1.0) * cosine + root),
                2.0 * amplitude * ((amplitude - 1.0) - (amplitude + 1.0) * cosine),
                amplitude * ((amplitude + 1.0) - (amplitude - 1.0) * cosine - root),
                (amplitude + 1.0) + (amplitude - 1.0) * cosine + root,
                -2.0 * ((amplitude - 1.0) + (amplitude + 1.0) * cosine),
                (amplitude + 1.0) + (amplitude - 1.0) * cosine - root);
        }
    }
};

struct DelayLine final {
    std::array<double, MaximumDelaySamples> samples{};
    std::size_t index = 0;
    std::size_t delaySamples = 0;

    void configure(double delayMs, double sampleRate) noexcept
    {
        const double requested = std::round(delayMs * sampleRate / 1'000.0);
        delaySamples = static_cast<std::size_t>(std::clamp(requested, 0.0, static_cast<double>(MaximumDelaySamples - 2)));
        reset();
    }

    void reset() noexcept
    {
        samples.fill(0.0);
        index = 0;
    }

    [[nodiscard]] double process(double input) noexcept
    {
        if (delaySamples == 0) return input;
        samples[index] = input;
        const std::size_t read = (index + MaximumDelaySamples - delaySamples) % MaximumDelaySamples;
        const double output = samples[read];
        index = (index + 1) % MaximumDelaySamples;
        return output;
    }
};

struct ChannelProcessor final {
    double inputGain = 1.0;
    double outputGain = 1.0;
    bool mute = false;
    bool solo = false;
    bool invert = false;
    DelayLine delay;
    std::array<Biquad, MaximumPassStages> highPass{};
    std::array<Biquad, MaximumPassStages> lowPass{};
    std::array<Biquad, MaximumFilterCount> equalizers{};
    std::size_t highPassCount = 0;
    std::size_t lowPassCount = 0;
    std::size_t equalizerCount = 0;

    void configure(const pf_channel_config_v1& config, double sampleRate) noexcept
    {
        inputGain = dbToLinear(config.input_trim_db);
        outputGain = dbToLinear(config.output_trim_db);
        mute = config.mute != 0;
        solo = config.solo != 0;
        invert = config.polarity_inverted != 0;
        delay.configure(config.delay_ms, sampleRate);
        highPassCount = config.high_pass.enabled != 0 ? static_cast<std::size_t>(config.high_pass.slope_db_per_octave / 12u) : 0;
        lowPassCount = config.low_pass.enabled != 0 ? static_cast<std::size_t>(config.low_pass.slope_db_per_octave / 12u) : 0;
        highPassCount = std::min(highPassCount, MaximumPassStages);
        lowPassCount = std::min(lowPassCount, MaximumPassStages);
        configurePass(highPass, highPassCount, config.high_pass.frequency_hz, true, sampleRate);
        configurePass(lowPass, lowPassCount, config.low_pass.frequency_hz, false, sampleRate);
        equalizerCount = std::min<std::size_t>(config.filter_count, MaximumFilterCount);
        for (std::size_t index = 0; index < equalizerCount; ++index) {
            const pf_filter_v1& filter = config.filters[index];
            Biquad& biquad = equalizers[index];
            if (filter.enabled == 0) {
                biquad = Biquad{};
            } else if (filter.shape == PF_FILTER_NOTCH) {
                biquad.setNotch(filter.frequency_hz, filter.q, sampleRate);
            } else if (filter.shape == PF_FILTER_LOW_SHELF) {
                biquad.setShelf(filter.frequency_hz, filter.gain_db, false, sampleRate);
            } else if (filter.shape == PF_FILTER_HIGH_SHELF) {
                biquad.setShelf(filter.frequency_hz, filter.gain_db, true, sampleRate);
            } else {
                biquad.setBell(filter.frequency_hz, filter.gain_db, filter.q, sampleRate);
            }
            biquad.reset();
        }
        for (std::size_t index = equalizerCount; index < equalizers.size(); ++index) equalizers[index] = Biquad{};
    }

    [[nodiscard]] double process(double input) noexcept
    {
        double sample = input * inputGain * (invert ? -1.0 : 1.0);
        sample = delay.process(sample);
        for (std::size_t index = 0; index < highPassCount; ++index) sample = highPass[index].process(sample);
        for (std::size_t index = 0; index < equalizerCount; ++index) sample = equalizers[index].process(sample);
        for (std::size_t index = 0; index < lowPassCount; ++index) sample = lowPass[index].process(sample);
        return sample * outputGain;
    }

private:
    static void configurePass(std::array<Biquad, MaximumPassStages>& filters, std::size_t count, double frequency, bool isHighPass, double sampleRate) noexcept
    {
        const std::size_t order = count * 2;
        for (std::size_t index = 0; index < filters.size(); ++index) {
            filters[index] = Biquad{};
            if (index >= count) continue;
            const double angle = std::numbers::pi * static_cast<double>(2 * index + 1) / static_cast<double>(2 * order);
            const double q = 1.0 / (2.0 * std::cos(angle));
            if (isHighPass) filters[index].setHighPass(frequency, q, sampleRate);
            else filters[index].setLowPass(frequency, q, sampleRate);
        }
    }
};

struct DampingChannel final {
    struct DynamicNotch final {
        Biquad filter;
        int band = -1;
        double gainDb = 0.0;
        double targetDb = 0.0;
    };

    std::array<Biquad, DampingBandCount> detectors{};
    std::array<double, DampingBandCount> envelopes{};
    std::array<double, DampingBandCount> frequencies{};
    std::array<double, DampingBandCount> scores{};
    std::array<DynamicNotch, DampingNotchCount> notches{};
    double sampleRate = 48'000.0;
    double sensitivityDb = 5.0;
    double maximumReductionDb = 6.0;
    double attackCoefficient = 0.0;
    double releaseCoefficient = 0.0;
    bool enabled = false;

    void configure(const pf_damping_config_v1& config, double configuredSampleRate) noexcept
    {
        enabled = config.enabled != 0;
        sampleRate = configuredSampleRate;
        sensitivityDb = config.sensitivity_db;
        maximumReductionDb = config.maximum_reduction_db;
        attackCoefficient = std::exp(-64.0 / (std::max(1.0, config.attack_ms) * 0.001 * sampleRate));
        releaseCoefficient = std::exp(-64.0 / (std::max(20.0, config.release_ms) * 0.001 * sampleRate));
        const double ratio = config.maximum_frequency_hz / config.minimum_frequency_hz;
        for (std::size_t index = 0; index < DampingBandCount; ++index) {
            const double fraction = static_cast<double>(index) / static_cast<double>(DampingBandCount - 1);
            frequencies[index] = config.minimum_frequency_hz * std::pow(ratio, fraction);
            detectors[index].setBandPass(frequencies[index], 4.0, sampleRate);
            detectors[index].reset();
            envelopes[index] = 0.0;
            scores[index] = 0.0;
        }
        for (DynamicNotch& notch : notches) notch = DynamicNotch{};
    }

    void analyze(double sample) noexcept
    {
        if (!enabled) return;
        for (std::size_t index = 0; index < DampingBandCount; ++index) {
            const double magnitude = std::abs(detectors[index].process(sample));
            const double coefficient = magnitude > envelopes[index] ? 0.82 : 0.995;
            envelopes[index] = coefficient * envelopes[index] + (1.0 - coefficient) * magnitude;
        }
    }

    void calculateScores() noexcept
    {
        if (!enabled) {
            scores.fill(0.0);
            return;
        }
        for (std::size_t index = 0; index < DampingBandCount; ++index) {
            const std::size_t lower = index == 0 ? index : index - 1;
            const std::size_t upper = index + 1 >= DampingBandCount ? index : index + 1;
            const double neighbourhood = (envelopes[lower] + envelopes[upper]) * 0.5 + Epsilon;
            const double prominence = 20.0 * std::log10((envelopes[index] + Epsilon) / neighbourhood);
            const double levelDb = linearToDb(envelopes[index]);
            scores[index] = levelDb > -70.0 ? std::max(0.0, prominence - sensitivityDb) : 0.0;
        }
    }

    void setTargets(const std::array<int, DampingNotchCount>& selected, const std::array<double, DampingNotchCount>& reductions) noexcept
    {
        for (std::size_t index = 0; index < DampingNotchCount; ++index) {
            DynamicNotch& notch = notches[index];
            const int selectedBand = selected[index];
            if (selectedBand >= 0 && selectedBand < static_cast<int>(DampingBandCount)) {
                if (notch.band != selectedBand && std::abs(notch.gainDb) < 0.25) notch.band = selectedBand;
                if (notch.band == selectedBand) notch.targetDb = -std::min(maximumReductionDb, reductions[index]);
            } else {
                notch.targetDb = 0.0;
            }
            const double coefficient = notch.targetDb < notch.gainDb ? attackCoefficient : releaseCoefficient;
            notch.gainDb = coefficient * notch.gainDb + (1.0 - coefficient) * notch.targetDb;
            if (notch.band >= 0) {
                notch.filter.setBell(frequencies[static_cast<std::size_t>(notch.band)], notch.gainDb, 5.0, sampleRate);
            }
        }
    }

    [[nodiscard]] double apply(double sample) noexcept
    {
        if (!enabled) return sample;
        for (DynamicNotch& notch : notches) {
            if (notch.band >= 0 && std::abs(notch.gainDb) > 0.001) sample = notch.filter.process(sample);
        }
        return sample;
    }
};

struct StereoDamping final {
    DampingChannel left;
    DampingChannel right;
    bool linked = true;
    std::uint32_t controlCounter = 0;

    void configure(const pf_damping_config_v1& config, double sampleRate) noexcept
    {
        left.configure(config, sampleRate);
        right.configure(config, sampleRate);
        linked = config.stereo_linked != 0;
        controlCounter = 0;
    }

    void process(double& leftSample, double& rightSample) noexcept
    {
        left.analyze(leftSample);
        right.analyze(rightSample);
        ++controlCounter;
        if (controlCounter >= 64) {
            controlCounter = 0;
            left.calculateScores();
            right.calculateScores();
            if (linked) {
                std::array<double, DampingBandCount> common{};
                for (std::size_t index = 0; index < common.size(); ++index) common[index] = std::max(left.scores[index], right.scores[index]);
                const auto [bands, reductions] = select(common);
                left.setTargets(bands, reductions);
                right.setTargets(bands, reductions);
            } else {
                const auto [leftBands, leftReductions] = select(left.scores);
                const auto [rightBands, rightReductions] = select(right.scores);
                left.setTargets(leftBands, leftReductions);
                right.setTargets(rightBands, rightReductions);
            }
        }
        leftSample = left.apply(leftSample);
        rightSample = right.apply(rightSample);
    }

private:
    using Selection = std::pair<std::array<int, DampingNotchCount>, std::array<double, DampingNotchCount>>;

    static Selection select(const std::array<double, DampingBandCount>& scores) noexcept
    {
        std::array<int, DampingNotchCount> selected{};
        std::array<double, DampingNotchCount> reductions{};
        selected.fill(-1);
        for (std::size_t slot = 0; slot < DampingNotchCount; ++slot) {
            double best = 0.0;
            int bestIndex = -1;
            for (std::size_t band = 0; band < DampingBandCount; ++band) {
                bool nearExisting = false;
                for (std::size_t prior = 0; prior < slot; ++prior) {
                    if (selected[prior] >= 0 && std::abs(selected[prior] - static_cast<int>(band)) < 2) nearExisting = true;
                }
                if (!nearExisting && scores[band] > best) {
                    best = scores[band];
                    bestIndex = static_cast<int>(band);
                }
            }
            if (bestIndex >= 0) {
                selected[slot] = bestIndex;
                reductions[slot] = best;
            }
        }
        return { selected, reductions };
    }
};

struct TruePeakLimiter final {
    std::array<double, LimiterDelaySamples> leftDelay{};
    std::array<double, LimiterDelaySamples> rightDelay{};
    std::array<double, 4> leftHistory{};
    std::array<double, 4> rightHistory{};
    std::array<double, 4> outputLeftHistory{};
    std::array<double, 4> outputRightHistory{};
    std::size_t delayIndex = 0;
    std::size_t lookaheadSamples = 0;
    double ceiling = dbToLinear(-1.0);
    double releaseCoefficient = 0.999;
    double gain = 1.0;
    double lastTruePeak = 0.0;
    bool enabled = true;

    void configure(const pf_limiter_config_v1& config, double sampleRate) noexcept
    {
        enabled = config.enabled != 0;
        ceiling = dbToLinear(config.ceiling_db_tp);
        releaseCoefficient = std::exp(-1.0 / (std::max(20.0, config.release_ms) * 0.001 * sampleRate));
        const double requested = std::round(config.lookahead_ms * sampleRate / 1'000.0);
        lookaheadSamples = static_cast<std::size_t>(std::clamp(requested, 1.0, static_cast<double>(LimiterDelaySamples - 2)));
        reset();
    }

    void reset() noexcept
    {
        leftDelay.fill(0.0);
        rightDelay.fill(0.0);
        leftHistory.fill(0.0);
        rightHistory.fill(0.0);
        outputLeftHistory.fill(0.0);
        outputRightHistory.fill(0.0);
        delayIndex = 0;
        gain = 1.0;
        lastTruePeak = 0.0;
    }

    void process(double& left, double& right) noexcept
    {
        if (!enabled) {
            lastTruePeak = std::max(std::abs(left), std::abs(right));
            gain = 1.0;
            return;
        }
        shift(leftHistory, left);
        shift(rightHistory, right);
        const double peak = std::max(interpolatedPeak(leftHistory), interpolatedPeak(rightHistory));
        const double desired = peak > ceiling ? ceiling / std::max(peak, Epsilon) : 1.0;
        if (desired < gain) gain = desired;
        else gain = releaseCoefficient * gain + (1.0 - releaseCoefficient);

        leftDelay[delayIndex] = left;
        rightDelay[delayIndex] = right;
        const std::size_t readIndex = (delayIndex + LimiterDelaySamples - lookaheadSamples) % LimiterDelaySamples;
        left = std::clamp(leftDelay[readIndex] * gain, -ceiling, ceiling);
        right = std::clamp(rightDelay[readIndex] * gain, -ceiling, ceiling);
        shift(outputLeftHistory, left);
        shift(outputRightHistory, right);
        lastTruePeak = std::max(interpolatedPeak(outputLeftHistory), interpolatedPeak(outputRightHistory));
        delayIndex = (delayIndex + 1) % LimiterDelaySamples;
    }

    [[nodiscard]] double reductionDb() const noexcept
    {
        return std::max(0.0, -20.0 * std::log10(std::max(gain, Epsilon)));
    }

private:
    static void shift(std::array<double, 4>& history, double sample) noexcept
    {
        history[0] = history[1];
        history[1] = history[2];
        history[2] = history[3];
        history[3] = sample;
    }

    static double interpolatedPeak(const std::array<double, 4>& history) noexcept
    {
        double peak = std::max(std::abs(history[1]), std::abs(history[2]));
        for (int phase = 1; phase < 4; ++phase) {
            const double t = static_cast<double>(phase) * 0.25;
            const double t2 = t * t;
            const double t3 = t2 * t;
            const double value = 0.5 * ((2.0 * history[1]) + (-history[0] + history[2]) * t +
                (2.0 * history[0] - 5.0 * history[1] + 4.0 * history[2] - history[3]) * t2 +
                (-history[0] + 3.0 * history[1] - 3.0 * history[2] + history[3]) * t3);
            peak = std::max(peak, std::abs(value));
        }
        return peak;
    }
};

struct LoudnessMeter final {
    std::array<Biquad, ChannelCount> shelf{};
    std::array<Biquad, ChannelCount> highPass{};
    double energy = 0.0;
    double coefficient = 0.9999;

    void configure(double sampleRate) noexcept
    {
        for (std::size_t channel = 0; channel < ChannelCount; ++channel) {
            shelf[channel].setShelf(1'681.974, 4.0, true, sampleRate);
            highPass[channel].setHighPass(38.135, 0.5, sampleRate);
        }
        coefficient = std::exp(-1.0 / (0.4 * sampleRate));
        energy = 0.0;
    }

    void process(double left, double right) noexcept
    {
        const double weightedLeft = highPass[0].process(shelf[0].process(left));
        const double weightedRight = highPass[1].process(shelf[1].process(right));
        const double instantaneous = weightedLeft * weightedLeft + weightedRight * weightedRight;
        energy = coefficient * energy + (1.0 - coefficient) * instantaneous;
    }

    [[nodiscard]] float momentaryLufs() const noexcept
    {
        return energy > 1.0e-12 ? static_cast<float>(-0.691 + 10.0 * std::log10(energy)) : -120.0F;
    }
};

struct Pipeline final {
    ChannelProcessor left;
    ChannelProcessor right;
    StereoDamping damping;
    TruePeakLimiter limiter;
    double masterGain = 1.0;
    double balance = 0.0;
    double width = 1.0;
    bool mono = false;
    bool bypass = false;

    void configure(const pf_config_v1& config, double sampleRate) noexcept
    {
        left.configure(config.left, sampleRate);
        right.configure(config.right, sampleRate);
        damping.configure(config.damping, sampleRate);
        limiter.configure(config.limiter, sampleRate);
        masterGain = dbToLinear(config.master_gain_db);
        balance = config.stereo.balance;
        width = config.stereo.width_percent / 100.0;
        mono = config.stereo.mono_audition != 0;
        bypass = config.bypass != 0;
    }

    void process(double inputLeft, double inputRight, double& outputLeft, double& outputRight) noexcept
    {
        if (bypass) {
            outputLeft = inputLeft;
            outputRight = inputRight;
            return;
        }
        double leftSample = left.process(inputLeft);
        double rightSample = right.process(inputRight);
        const bool anySolo = left.solo || right.solo;
        if (left.mute || (anySolo && !left.solo)) leftSample = 0.0;
        if (right.mute || (anySolo && !right.solo)) rightSample = 0.0;
        damping.process(leftSample, rightSample);

        if (mono) {
            const double sum = (leftSample + rightSample) * 0.5;
            leftSample = sum;
            rightSample = sum;
        } else {
            const double mid = (leftSample + rightSample) * 0.5;
            const double side = (leftSample - rightSample) * 0.5 * width;
            leftSample = mid + side;
            rightSample = mid - side;
        }
        if (balance > 0.0) leftSample *= std::cos(balance * std::numbers::pi * 0.5);
        else if (balance < 0.0) rightSample *= std::cos(-balance * std::numbers::pi * 0.5);
        leftSample *= masterGain;
        rightSample *= masterGain;
        limiter.process(leftSample, rightSample);
        outputLeft = leftSample;
        outputRight = rightSample;
    }
};

struct TelemetryAccumulator final {
    pf_telemetry_v1 working{};
    std::array<pf_telemetry_v1, 3> published{};
    std::array<std::atomic<std::uint32_t>, 3> readers{};
    std::atomic<int> publishedIndex{0};
    std::uint32_t waveformIndex = 0;
    std::uint64_t sequence = 0;
    std::uint64_t totalFrames = 0;
    double peakLeft = 0.0;
    double peakRight = 0.0;
    double squareLeft = 0.0;
    double squareRight = 0.0;
    double cross = 0.0;
    double truePeak = 0.0;
    double limiterReduction = 0.0;
    std::uint32_t sampleRate = 48'000;

    void configure(std::uint32_t configuredSampleRate) noexcept
    {
        sampleRate = configuredSampleRate;
        reset();
    }

    void reset() noexcept
    {
        working = {};
        working.size = sizeof(pf_telemetry_v1);
        working.version = PF_ABI_VERSION;
        for (pf_telemetry_v1& item : published) item = working;
        for (auto& reader : readers) reader.store(0, std::memory_order_relaxed);
        publishedIndex.store(0, std::memory_order_release);
        waveformIndex = 0;
        sequence = 0;
        totalFrames = 0;
        peakLeft = peakRight = squareLeft = squareRight = cross = truePeak = limiterReduction = 0.0;
    }

    void resetRealtime() noexcept
    {
        working = {};
        working.size = sizeof(pf_telemetry_v1);
        working.version = PF_ABI_VERSION;
        waveformIndex = 0;
        peakLeft = peakRight = squareLeft = squareRight = cross = truePeak = limiterReduction = 0.0;
    }

    void add(double preLeft, double preRight, double postLeft, double postRight, const Pipeline& pipeline, float loudness) noexcept
    {
        if (waveformIndex < PF_WAVEFORM_CAPACITY) {
            working.pre_left[waveformIndex] = static_cast<float>(preLeft);
            working.pre_right[waveformIndex] = static_cast<float>(preRight);
            working.post_left[waveformIndex] = static_cast<float>(postLeft);
            working.post_right[waveformIndex] = static_cast<float>(postRight);
        }
        peakLeft = std::max(peakLeft, std::abs(postLeft));
        peakRight = std::max(peakRight, std::abs(postRight));
        squareLeft += postLeft * postLeft;
        squareRight += postRight * postRight;
        cross += postLeft * postRight;
        truePeak = std::max(truePeak, pipeline.limiter.lastTruePeak);
        limiterReduction = std::max(limiterReduction, pipeline.limiter.reductionDb());
        ++waveformIndex;
        ++totalFrames;
        if (waveformIndex >= PF_WAVEFORM_CAPACITY) publish(loudness);
    }

    pf_result read(pf_telemetry_v1& destination) noexcept
    {
        for (int attempt = 0; attempt < 4; ++attempt) {
            const int index = publishedIndex.load(std::memory_order_acquire);
            readers[static_cast<std::size_t>(index)].fetch_add(1, std::memory_order_acq_rel);
            if (index != publishedIndex.load(std::memory_order_acquire)) {
                readers[static_cast<std::size_t>(index)].fetch_sub(1, std::memory_order_release);
                continue;
            }
            destination = published[static_cast<std::size_t>(index)];
            readers[static_cast<std::size_t>(index)].fetch_sub(1, std::memory_order_release);
            return PF_SUCCESS;
        }
        return PF_BUSY;
    }

private:
    void publish(float loudness) noexcept
    {
        const double frameCount = static_cast<double>(waveformIndex);
        working.size = sizeof(pf_telemetry_v1);
        working.version = PF_ABI_VERSION;
        working.sequence = ++sequence;
        working.monotonic_nanoseconds = totalFrames * 1'000'000'000ULL / sampleRate;
        working.sample_rate = sampleRate;
        working.frame_count = waveformIndex;
        working.peak_left_dbfs = linearToDb(peakLeft);
        working.peak_right_dbfs = linearToDb(peakRight);
        working.rms_left_dbfs = linearToDb(std::sqrt(squareLeft / frameCount));
        working.rms_right_dbfs = linearToDb(std::sqrt(squareRight / frameCount));
        working.true_peak_dbtp = linearToDb(truePeak);
        working.momentary_lufs = loudness;
        working.limiter_reduction_db = static_cast<float>(limiterReduction);
        const double denominator = std::sqrt(squareLeft * squareRight);
        working.correlation = denominator > Epsilon ? static_cast<float>(std::clamp(cross / denominator, -1.0, 1.0)) : 1.0F;

        const int current = publishedIndex.load(std::memory_order_relaxed);
        int target = -1;
        for (int candidate = 0; candidate < 3; ++candidate) {
            if (candidate != current && readers[static_cast<std::size_t>(candidate)].load(std::memory_order_acquire) == 0) {
                target = candidate;
                break;
            }
        }
        if (target >= 0) {
            published[static_cast<std::size_t>(target)] = working;
            publishedIndex.store(target, std::memory_order_release);
        }
        waveformIndex = 0;
        peakLeft = peakRight = squareLeft = squareRight = cross = truePeak = limiterReduction = 0.0;
    }
};

[[nodiscard]] bool inRange(double value, double minimum, double maximum) noexcept
{
    return std::isfinite(value) && value >= minimum && value <= maximum;
}

pf_result validateChannel(const pf_channel_config_v1& channel, std::uint32_t sampleRate) noexcept
{
    const double maximumFrequency = std::min(24'000.0, static_cast<double>(sampleRate) * 0.45);
    if (!inRange(channel.input_trim_db, -24.0, 12.0) || !inRange(channel.output_trim_db, -24.0, 12.0) || !inRange(channel.delay_ms, 0.0, 20.0)) return PF_INVALID_CONFIG;
    if (channel.filter_count > PF_MAX_FILTERS) return PF_INVALID_CONFIG;
    const auto validPass = [maximumFrequency](const pf_pass_filter_v1& filter) {
        return inRange(filter.frequency_hz, 10.0, maximumFrequency) &&
            (filter.slope_db_per_octave == 12 || filter.slope_db_per_octave == 24 || filter.slope_db_per_octave == 36 || filter.slope_db_per_octave == 48);
    };
    if (!validPass(channel.high_pass) || !validPass(channel.low_pass)) return PF_INVALID_CONFIG;
    for (std::uint32_t index = 0; index < channel.filter_count; ++index) {
        const pf_filter_v1& filter = channel.filters[index];
        if (filter.shape > PF_FILTER_HIGH_SHELF || !inRange(filter.frequency_hz, 10.0, maximumFrequency) || !inRange(filter.gain_db, -24.0, 18.0) || !inRange(filter.q, 0.1, 24.0)) return PF_INVALID_CONFIG;
    }
    return PF_SUCCESS;
}

} // namespace

struct DspEngine::Impl final {
    std::array<Pipeline, 3> pipelines{};
    std::atomic<int> activeIndex{0};
    std::atomic<int> pendingIndex{-1};
    std::atomic<int> retiringIndex{-1};
    std::atomic<bool> resetRequested{false};
    std::uint32_t fadeRemaining = 0;
    LoudnessMeter loudness;
    TelemetryAccumulator telemetry;
};

DspEngine::DspEngine() : impl_(new (std::nothrow) Impl{}) {}
DspEngine::~DspEngine() { delete impl_; }

pf_result DspEngine::prepare(std::uint32_t sampleRate, std::uint32_t maximumBlockFrames) noexcept
{
    if (impl_ == nullptr) return PF_OUT_OF_MEMORY;
    if (sampleRate < 44'100 || sampleRate > 192'000 || maximumBlockFrames == 0 || maximumBlockFrames > 8'192) return PF_INVALID_ARGUMENT;
    sampleRate_ = sampleRate;
    maximumBlockFrames_ = maximumBlockFrames;
    pf_config_v1 config{};
    initializeConfig(config);
    for (Pipeline& pipeline : impl_->pipelines) pipeline.configure(config, static_cast<double>(sampleRate));
    impl_->activeIndex.store(0, std::memory_order_release);
    impl_->pendingIndex.store(-1, std::memory_order_release);
    impl_->retiringIndex.store(-1, std::memory_order_release);
    impl_->resetRequested.store(false, std::memory_order_release);
    impl_->fadeRemaining = 0;
    impl_->loudness.configure(static_cast<double>(sampleRate));
    impl_->telemetry.configure(sampleRate);
    return PF_SUCCESS;
}

pf_result DspEngine::applyConfig(const pf_config_v1& config) noexcept
{
    if (impl_ == nullptr) return PF_OUT_OF_MEMORY;
    if (sampleRate_ == 0) return PF_NOT_READY;
    const pf_result validation = validateConfig(config, sampleRate_);
    if (validation != PF_SUCCESS) return validation;
    if (impl_->pendingIndex.load(std::memory_order_acquire) >= 0) return PF_BUSY;
    const int active = impl_->activeIndex.load(std::memory_order_acquire);
    const int retiring = impl_->retiringIndex.load(std::memory_order_acquire);
    int candidate = -1;
    for (int index = 0; index < 3; ++index) {
        if (index != active && index != retiring) {
            candidate = index;
            break;
        }
    }
    if (candidate < 0) return PF_BUSY;
    impl_->pipelines[static_cast<std::size_t>(candidate)].configure(config, static_cast<double>(sampleRate_));
    int expected = -1;
    if (!impl_->pendingIndex.compare_exchange_strong(expected, candidate, std::memory_order_release, std::memory_order_relaxed)) return PF_BUSY;
    return PF_SUCCESS;
}

pf_result DspEngine::process(const float* input, float* output, std::uint32_t frameCount) noexcept
{
    if (impl_ == nullptr) return PF_OUT_OF_MEMORY;
    if (sampleRate_ == 0) return PF_NOT_READY;
    if (input == nullptr || output == nullptr || frameCount == 0 || frameCount > maximumBlockFrames_) return PF_INVALID_ARGUMENT;
    if (impl_->resetRequested.exchange(false, std::memory_order_acq_rel)) {
        impl_->loudness.configure(static_cast<double>(sampleRate_));
        impl_->telemetry.resetRealtime();
    }
    if (impl_->fadeRemaining == 0) {
        const int pending = impl_->pendingIndex.exchange(-1, std::memory_order_acq_rel);
        if (pending >= 0) {
            const int old = impl_->activeIndex.exchange(pending, std::memory_order_acq_rel);
            impl_->retiringIndex.store(old, std::memory_order_release);
            impl_->fadeRemaining = CrossfadeFrames;
        }
    }
    const int activeIndex = impl_->activeIndex.load(std::memory_order_acquire);
    Pipeline& active = impl_->pipelines[static_cast<std::size_t>(activeIndex)];
    const int retiringIndex = impl_->retiringIndex.load(std::memory_order_acquire);
    Pipeline* retiring = retiringIndex >= 0 ? &impl_->pipelines[static_cast<std::size_t>(retiringIndex)] : nullptr;

    for (std::uint32_t frame = 0; frame < frameCount; ++frame) {
        const std::size_t sampleIndex = static_cast<std::size_t>(frame) * 2;
        const double inputLeft = std::isfinite(input[sampleIndex]) ? static_cast<double>(input[sampleIndex]) : 0.0;
        const double inputRight = std::isfinite(input[sampleIndex + 1]) ? static_cast<double>(input[sampleIndex + 1]) : 0.0;
        double outputLeft = 0.0;
        double outputRight = 0.0;
        active.process(inputLeft, inputRight, outputLeft, outputRight);
        if (impl_->fadeRemaining > 0 && retiring != nullptr) {
            double oldLeft = 0.0;
            double oldRight = 0.0;
            retiring->process(inputLeft, inputRight, oldLeft, oldRight);
            const double mix = 1.0 - static_cast<double>(impl_->fadeRemaining) / static_cast<double>(CrossfadeFrames);
            outputLeft = oldLeft + (outputLeft - oldLeft) * mix;
            outputRight = oldRight + (outputRight - oldRight) * mix;
            --impl_->fadeRemaining;
            if (impl_->fadeRemaining == 0) {
                impl_->retiringIndex.store(-1, std::memory_order_release);
                retiring = nullptr;
            }
        }
        output[sampleIndex] = static_cast<float>(outputLeft);
        output[sampleIndex + 1] = static_cast<float>(outputRight);
        impl_->loudness.process(outputLeft, outputRight);
        impl_->telemetry.add(inputLeft, inputRight, outputLeft, outputRight, active, impl_->loudness.momentaryLufs());
    }
    return PF_SUCCESS;
}

pf_result DspEngine::readTelemetry(pf_telemetry_v1& telemetry) noexcept
{
    if (impl_ == nullptr) return PF_OUT_OF_MEMORY;
    if (telemetry.size < sizeof(pf_telemetry_v1) || telemetry.version != PF_ABI_VERSION) return PF_INVALID_ARGUMENT;
    return impl_->telemetry.read(telemetry);
}

void DspEngine::reset() noexcept
{
    if (impl_ == nullptr || sampleRate_ == 0) return;
    impl_->resetRequested.store(true, std::memory_order_release);
}

pf_result validateConfig(const pf_config_v1& config, std::uint32_t sampleRate) noexcept
{
    if (config.size < sizeof(pf_config_v1) || config.version != PF_ABI_VERSION || sampleRate < 44'100 || sampleRate > 192'000) return PF_INVALID_ARGUMENT;
    if (validateChannel(config.left, sampleRate) != PF_SUCCESS || validateChannel(config.right, sampleRate) != PF_SUCCESS) return PF_INVALID_CONFIG;
    const double maximumFrequency = std::min(20'000.0, static_cast<double>(sampleRate) * 0.45);
    if (!inRange(config.master_gain_db, -24.0, 18.0) || !inRange(config.stereo.balance, -1.0, 1.0) || !inRange(config.stereo.width_percent, 0.0, 200.0)) return PF_INVALID_CONFIG;
    if (!inRange(config.damping.sensitivity_db, 0.0, 18.0) || !inRange(config.damping.minimum_frequency_hz, 20.0, maximumFrequency) ||
        !inRange(config.damping.maximum_frequency_hz, 20.0, maximumFrequency) || config.damping.maximum_frequency_hz <= config.damping.minimum_frequency_hz ||
        !inRange(config.damping.attack_ms, 1.0, 100.0) || !inRange(config.damping.release_ms, 20.0, 1'000.0) || !inRange(config.damping.maximum_reduction_db, 0.0, 12.0)) return PF_INVALID_CONFIG;
    if (!inRange(config.limiter.ceiling_db_tp, -12.0, 0.0) || !inRange(config.limiter.release_ms, 20.0, 1'000.0) || !inRange(config.limiter.lookahead_ms, 0.5, 5.0)) return PF_INVALID_CONFIG;
    if (config.master_gain_db > 0.0 && config.limiter.enabled == 0 && config.limiter.expert_bypass_confirmed == 0) return PF_INVALID_CONFIG;
    return PF_SUCCESS;
}

void initializeConfig(pf_config_v1& config) noexcept
{
    std::memset(&config, 0, sizeof(config));
    config.size = sizeof(config);
    config.version = PF_ABI_VERSION;
    const std::array<double, PF_MAX_FILTERS> frequencies{31.25, 62.5, 125.0, 250.0, 500.0, 1'000.0, 2'000.0, 4'000.0, 8'000.0, 16'000.0, 10'000.0, 80.0};
    auto initializeChannel = [&frequencies](pf_channel_config_v1& channel) {
        channel.high_pass.frequency_hz = 20.0;
        channel.high_pass.slope_db_per_octave = 24;
        channel.low_pass.frequency_hz = 20'000.0;
        channel.low_pass.slope_db_per_octave = 24;
        channel.filter_count = PF_MAX_FILTERS;
        for (std::size_t index = 0; index < frequencies.size(); ++index) {
            channel.filters[index].enabled = index < 10 ? 1u : 0u;
            channel.filters[index].shape = PF_FILTER_BELL;
            channel.filters[index].frequency_hz = frequencies[index];
            channel.filters[index].gain_db = 0.0;
            channel.filters[index].q = index < 10 ? 1.1 : 0.8;
        }
    };
    initializeChannel(config.left);
    initializeChannel(config.right);
    config.damping.stereo_linked = 1;
    config.damping.sensitivity_db = 5.0;
    config.damping.minimum_frequency_hz = 120.0;
    config.damping.maximum_frequency_hz = 16'000.0;
    config.damping.attack_ms = 8.0;
    config.damping.release_ms = 180.0;
    config.damping.maximum_reduction_db = 6.0;
    config.stereo.channels_linked = 1;
    config.stereo.width_percent = 100.0;
    config.limiter.enabled = 1;
    config.limiter.ceiling_db_tp = -1.0;
    config.limiter.release_ms = 80.0;
    config.limiter.lookahead_ms = 2.0;
}

} // namespace pulseforge
