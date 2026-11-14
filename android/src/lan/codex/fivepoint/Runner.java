package lan.codex.fivepoint;

import java.util.ArrayList;
import java.util.List;

final class Runner {
    enum State { IDLE, COUNTDOWN, RUNNING, PAUSED, STOPPING }
    interface Scheduler {
        long now();
        void post(Runnable next, long delay);
        void remove(Runnable next);
    }
    interface Touch {
        boolean tap(Config.Point point, Result result);
    }
    interface Result { void done(boolean delivered); }

    private final Scheduler scheduler;
    private final Touch touch;
    private final Runnable update;
    private final Runnable next = this::step;
    private final Runnable deadline = () -> stop("已达到指定运行时间");
    private final List<Config.Point> points = new ArrayList<>();
    private Config config;
    private State state = State.IDLE;
    private boolean inFlight;
    private int pointIndex, tapIndex;
    private long clicks, rounds, elapsed, segmentStart, due, pauseRemaining, countdownEnd;
    private Config.Point latest;
    private String notice = "待机";

    Runner(Scheduler scheduler, Touch touch, Runnable update) {
        this.scheduler = scheduler;
        this.touch = touch;
        this.update = update;
    }

    State state() { return state; }
    long clicks() { return clicks; }
    long rounds() { return rounds; }
    long elapsed() { return elapsed + (state == State.RUNNING ? scheduler.now() - segmentStart : 0); }
    long countdownLeft() { return Math.max(0, (countdownEnd - scheduler.now() + 999) / 1000); }
    Config.Point latest() { return latest; }
    String notice() { return notice; }

    void start(Config settings) {
        if (state != State.IDLE) return;
        points.clear();
        for (Config.Point point : settings.points) if (point.enabled) points.add(point);
        if (points.isEmpty()) throw new IllegalArgumentException("请先启用至少一个点位。");
        config = settings;
        latest = null;
        pointIndex = tapIndex = 0;
        clicks = rounds = elapsed = 0;
        notice = "运行中";
        countdownEnd = scheduler.now() + config.countdown * 1000L;
        state = State.COUNTDOWN;
        schedule(config.countdown * 1000L);
        update.run();
    }

    void togglePause() {
        if (state == State.RUNNING) {
            elapsed = elapsed();
            pauseRemaining = Math.max(0, due - scheduler.now());
            state = State.PAUSED;
            scheduler.remove(next);
            scheduler.remove(deadline);
        } else if (state == State.PAUSED) {
            beginActive();
            if (!inFlight) schedule(pauseRemaining);
        } else {
            return;
        }
        update.run();
    }

    void stop(String reason) {
        if (state == State.IDLE || state == State.STOPPING) return;
        elapsed = elapsed();
        scheduler.remove(next);
        scheduler.remove(deadline);
        notice = reason;
        state = inFlight ? State.STOPPING : State.IDLE;
        update.run();
    }

    private void beginActive() {
        state = State.RUNNING;
        segmentStart = scheduler.now();
        if (config.mode == Config.Mode.TIME) {
            scheduler.post(deadline, Math.max(0, config.seconds * 1000L - elapsed));
        }
    }

    private void schedule(long delay) {
        due = scheduler.now() + delay;
        scheduler.post(next, delay);
    }

    private void step() {
        if (state == State.COUNTDOWN) {
            beginActive();
            update.run();
        }
        if (state != State.RUNNING || inFlight) return;
        if (config.mode == Config.Mode.TIME && elapsed() >= config.seconds * 1000L) {
            stop("已达到指定运行时间");
            return;
        }
        Config.Point p = points.get(pointIndex);
        inFlight = true;
        if (!touch.tap(p, delivered -> complete(p, delivered))) {
            inFlight = false;
            stop("系统拒绝手势，请检查无障碍服务或点位");
        }
    }

    private void complete(Config.Point p, boolean delivered) {
        inFlight = false;
        long delay = 0;
        if (delivered) {
            latest = p;
            clicks++;
            tapIndex++;
            if (tapIndex == p.repeat * (p.doubleTap ? 2 : 1)) {
                tapIndex = 0;
                pointIndex++;
                if (pointIndex == points.size()) {
                    rounds++;
                    pointIndex = 0;
                    delay = config.roundMs;
                } else {
                    delay = config.pointMs;
                }
            } else if (p.doubleTap) {
                delay = 35;
            }
        }
        if (state == State.STOPPING) {
            state = State.IDLE;
        } else if (!delivered) {
            stop("手势被系统取消，请检查屏幕状态后重新开始");
            return;
        } else if (config.mode == Config.Mode.ROUNDS && rounds >= config.rounds) {
            stop("已完成指定轮数");
            return;
        } else if (state == State.PAUSED) {
            pauseRemaining = delay;
        } else if (state == State.RUNNING) {
            schedule(delay);
        }
        update.run();
    }
}
