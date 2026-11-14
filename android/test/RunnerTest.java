package lan.codex.fivepoint;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;
import java.util.PriorityQueue;

public final class RunnerTest {
    private static final class Clock implements Runner.Scheduler {
        private static final class Event {
            final Runnable task;
            final long due, id;
            Event(Runnable task, long due, long id) { this.task = task; this.due = due; this.id = id; }
        }
        final PriorityQueue<Event> events = new PriorityQueue<>(Comparator
            .comparingLong((Event e) -> e.due).thenComparingLong(e -> e.id));
        long time, queued;
        public long now() { return time; }
        public void post(Runnable task, long delay) { events.add(new Event(task, time + delay, queued++)); }
        public void remove(Runnable next) { events.removeIf(e -> e.task == next); }
        void advance(long duration) {
            long until = time + duration;
            while (!events.isEmpty() && events.peek().due <= until) {
                Event event = events.remove();
                time = event.due;
                event.task.run();
            }
            time = until;
        }
    }

    private static final class Fixture {
        final Clock clock = new Clock();
        final StringBuilder sequence = new StringBuilder();
        boolean accept = true, deliver = true;
        final Runner runner = new Runner(clock, (p, cb) -> {
            if (!accept) return false;
            sequence.append(p.name);
            clock.post(() -> cb.done(deliver), 16);
            return true;
        }, () -> {});
    }

    private static Config config(Config.Mode mode, int countdown, int points, int roundMs) {
        List<Config.Point> rows = new ArrayList<>(Arrays.asList(
            new Config.Point("A", 5, 5, 1, true, false),
            new Config.Point("B", 10, 10, 2, true, false),
            new Config.Point("C", 15, 15, 1, true, true),
            new Config.Point("D", 20, 20, 1, true, false),
            new Config.Point("E", 25, 25, 1, true, false)));
        return new Config(rows.subList(0, points), 0, roundMs, countdown, mode, 2, 1, 100, 100, 0);
    }

    private static void check(boolean result, String name) {
        if (!result) throw new AssertionError(name);
        System.out.println("PASS " + name);
    }

    public static void main(String[] args) {
        Fixture f = new Fixture();
        f.runner.start(config(Config.Mode.ROUNDS, 3, 5, 0));
        f.clock.advance(2999);
        check(f.runner.clicks() == 0 && f.runner.state() == Runner.State.COUNTDOWN, "no click during countdown");
        f.clock.advance(2000);
        check(f.sequence.toString().equals("ABBCCDEABBCCDE") && f.runner.rounds() == 2
            && f.runner.clicks() == 14 && f.runner.state() == Runner.State.IDLE, "five-point order, count, double tap and finite rounds");

        f = new Fixture();
        f.runner.start(config(Config.Mode.INFINITE, 0, 5, 0));
        f.clock.advance(2);
        f.runner.togglePause();
        f.clock.advance(200);
        check(f.runner.clicks() == 1 && f.runner.elapsed() == 2 && f.sequence.toString().equals("A"),
            "pause allows only the active touch to finish");
        f.runner.togglePause();
        f.clock.advance(35);
        check(f.sequence.toString().equals("ABBC"), "resume continues in order");
        f.runner.stop("stop");
        f.runner.start(config(Config.Mode.INFINITE, 0, 5, 0));
        f.clock.advance(300);
        check(f.runner.state() == Runner.State.IDLE && f.sequence.toString().equals("ABBC"),
            "stop prevents further touches and overlapping starts");
        f.runner.start(config(Config.Mode.INFINITE, 0, 5, 0));
        f.clock.advance(20);
        check(f.sequence.toString().equals("ABBCAB"), "can restart after stop");

        f = new Fixture();
        f.runner.start(config(Config.Mode.TIME, 0, 1, 5000));
        f.clock.advance(1500);
        check(f.runner.elapsed() == 1000 && f.runner.clicks() == 1 && f.runner.state() == Runner.State.IDLE,
            "time limit interrupts a longer scheduled delay");

        f = new Fixture();
        Config base = config(Config.Mode.ROUNDS, 0, 5, 0);
        List<Config.Point> reordered = Arrays.asList(base.points.get(4),
            new Config.Point("A", 5, 5, 1, false, false), base.points.get(1));
        f.runner.start(base.withPoints(reordered, 100, 100, 0));
        f.clock.advance(1000);
        check(f.sequence.toString().equals("EBBEBB"), "reordering and disabled entries");

        f = new Fixture();
        f.deliver = false;
        f.runner.start(config(Config.Mode.INFINITE, 0, 5, 0));
        f.clock.advance(100);
        check(f.runner.clicks() == 0 && f.runner.state() == Runner.State.IDLE, "cancelled gesture stops safely");
        f = new Fixture();
        f.accept = false;
        f.runner.start(config(Config.Mode.INFINITE, 0, 5, 0));
        f.clock.advance(100);
        check(f.runner.clicks() == 0 && f.runner.state() == Runner.State.IDLE, "rejected gesture stops safely");

        f = new Fixture();
        f.runner.start(config(Config.Mode.INFINITE, 3, 5, 0));
        f.clock.advance(10);
        f.runner.stop("stop");
        f.clock.advance(4000);
        check(f.sequence.length() == 0, "stop cancels countdown");
    }
}
