// Admin dashboard trends chart.
//
// The x axis is real time (a linear scale over epoch milliseconds), not a
// category axis: snapshots are irregular (every few minutes in some stretches,
// hourly or daily in others), and spacing points by index squeezed a week of
// sparse data into a few pixels next to a dense day. Dates are formatted here
// so no date adapter has to be loaded.
window.AdminChart = {
    render: function(canvasId, timestampsMs, datasets, range, windowMs) {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;

        if (ctx._chartInstance) {
            ctx._chartInstance.destroy();
        }

        // The axis always covers the selected window ending now, so a short range
        // with few snapshots shows where they sit instead of stretching them out.
        const last = Date.now();
        const first = last - windowMs;
        const span = windowMs;
        const tickFormat = AdminChart.tickFormatter(range, span);

        ctx._chartInstance = new Chart(ctx, {
            type: 'line',
            data: {
                datasets: datasets.map(ds => ({
                    label: ds.label,
                    borderColor: ds.borderColor,
                    backgroundColor: ds.backgroundColor,
                    data: ds.data.map((y, i) => ({ x: timestampsMs[i], y: y })),
                    stepped: 'before',
                    yAxisID: ds.label === 'Posts' ? 'y' : 'y2',
                    pointRadius: timestampsMs.length <= 60 ? 3 : 0,
                    borderWidth: 2
                }))
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: false,
                interaction: { intersect: false, mode: 'nearest', axis: 'x' },
                scales: {
                    x: {
                        type: 'linear',
                        min: first,
                        max: last,
                        ticks: {
                            color: '#8b949e',
                            maxTicksLimit: 8,
                            maxRotation: 45,
                            callback: value => tickFormat(new Date(value))
                        },
                        grid: { color: '#1e2636' }
                    },
                    y: {
                        title: { display: true, text: 'Posts', color: '#79b8ff' },
                        ticks: { color: '#8b949e' },
                        grid: { color: '#1e2636' }
                    },
                    y2: {
                        position: 'right',
                        beginAtZero: true,
                        title: { display: true, text: 'Feeds / Users', color: '#3fb950' },
                        ticks: { color: '#8b949e', precision: 0 },
                        grid: { drawOnChartArea: false }
                    }
                },
                plugins: {
                    legend: { labels: { color: '#c9d1d9' } },
                    tooltip: {
                        backgroundColor: '#0f1520',
                        titleColor: '#c9d1d9',
                        bodyColor: '#c9d1d9',
                        borderColor: '#1e2636',
                        borderWidth: 1,
                        callbacks: {
                            title: items => items.length ? AdminChart.full(new Date(items[0].parsed.x)) : ''
                        }
                    }
                }
            }
        });
    },

    // Short axis labels: time of day for short spans, date for long ones.
    tickFormatter: function(range, spanMs) {
        const hour = 3600000, day = 24 * hour;
        if (range === '1h' || spanMs <= 2 * hour) {
            return d => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        }
        if (range === '1d' || spanMs <= 2 * day) {
            return d => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        }
        if (range === '7d' || spanMs <= 14 * day) {
            return d => d.toLocaleDateString([], { month: 'numeric', day: 'numeric' }) + ' ' +
                        d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
        }
        if (range === '30d' || spanMs <= 60 * day) {
            return d => d.toLocaleDateString([], { month: 'numeric', day: 'numeric' });
        }
        return d => d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
    },

    full: function(d) {
        return d.toLocaleString([], { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
    }
};
