window.AdminChart = {
    render: function(canvasId, labels, datasets) {
        const ctx = document.getElementById(canvasId);
        if (!ctx) return;

        // Destroy existing chart if any
        if (ctx._chartInstance) {
            ctx._chartInstance.destroy();
        }

        ctx._chartInstance = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: datasets.map(ds => ({
                    ...ds,
                    tension: 0.3,
                    pointRadius: 0,
                    borderWidth: 2
                }))
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { intersect: false, mode: 'index' },
                scales: {
                    x: {
                        ticks: { color: '#8b949e', maxTicksAuto: 8 },
                        grid: { color: '#1e2636' }
                    },
                    y: {
                        ticks: { color: '#8b949e' },
                        grid: { color: '#1e2636' }
                    }
                },
                plugins: {
                    legend: { labels: { color: '#c9d1d9' } },
                    tooltip: {
                        backgroundColor: '#0f1520',
                        titleColor: '#c9d1d9',
                        bodyColor: '#c9d1d9',
                        borderColor: '#1e2636',
                        borderWidth: 1
                    }
                }
            }
        });
    }
};
