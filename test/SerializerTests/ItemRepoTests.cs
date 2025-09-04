namespace SerializerTests;
using RssApp.Serialization;
using RssApp.Contracts;
using RssApp.RssClient;
using Microsoft.Extensions.DependencyInjection;
using RssApp.ComponentServices;
using Microsoft.Extensions.Logging;
using Moq;
using RssApp.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using RssReader.Server.Services;

[TestClass]
public sealed class ItemRepoTests
{
    [TestMethod]
    public async Task MarkItemAsRead_Should_Update_Item()
    {
        if (File.Exists("tests.db"))
        {
            File.Delete("tests.db");
        }

        var userRepo = new SQLiteUserRepository(
            $"Data Source=tests.db",
            new NullLogger<SQLiteUserRepository>());
        userRepo.AddUser("testUser", 0);
        var feedRepo = new SQLiteFeedRepository(
            $"Data Source=tests.db",
            new NullLogger<SQLiteFeedRepository>());
        feedRepo.AddFeed(new NewsFeed(1, "https://feeds.propublica.org/propublica/main", 0));

        var user = new RssUser("testUser", 0);

        var serviceCollection = new ServiceCollection();
        serviceCollection
        .AddLogging(loggingBuilder =>
        {
            loggingBuilder.ClearProviders();
            loggingBuilder.AddConsole();
            loggingBuilder.AddDebug();
        })
        .AddSingleton<IFeedRepository>(feedRepo)
        .AddSingleton<IUserRepository>(userRepo)
        .AddSingleton<IItemRepository>(sb =>
        {
            return new SQLiteItemRepository(
                $"Data Source=tests.db",
                sb.GetRequiredService<ILogger<SQLiteItemRepository>>(),
                sb.GetRequiredService<IFeedRepository>(),
                sb.GetRequiredService<IUserRepository>(),
                sb.GetRequiredService<FeedThumbnailRetriever>());
        });

        var provider = serviceCollection.BuildServiceProvider();
        var itemRepo = provider.GetRequiredService<IItemRepository>();
        var item = new NewsFeedItem("0", 0, "abc", "https://www.propublica.org/article/forest-service-wildland-firefighters-staffing", null, null, "abc", null);

        item.FeedUrl = "https://feeds.propublica.org/propublica/main";

        await itemRepo.AddItemsAsync(new[] { item });

        item = itemRepo.GetItem(user, item.Href);
        item.FeedUrl = "https://feeds.propublica.org/propublica/main";
        itemRepo.MarkAsRead(item, true, user);

        item = itemRepo.GetItem(user, item.Href);
        Assert.IsTrue(item.IsRead);
    }

    private string GetContent()
    {
        return """
                <p class="byline">
                by <a class="name" href="https://www.propublica.org/people/david-armstrong">David Armstrong</a>, <a class="name" href="https://www.propublica.org/people/eric-umansky">Eric Umansky</a> and <a class="name" href="https://www.propublica.org/people/vernal-coleman">Vernal Coleman</a>                </p>

                <p>ProPublica is a nonprofit newsroom that investigates abuses of power. Sign up to receive <a href="https://www.propublica.org/newsletters/the-big-story?source=54G&amp;placement=top-note&amp;region=national">our biggest stories</a> as soon as they’re published.</p>
                        <p data-pp-blocktype="copy" data-pp-id="2.0">Veterans hospitals are struggling to replace hundreds of doctors and nurses who have left the health care system this year as the Trump administration pursues its pledge to simultaneously slash Department of Veterans Affairs staff and improve care.</p>
                        <p data-pp-blocktype="copy" data-pp-id="2.1">Many job applicants are turning down offers, worried that the positions are not stable and uneasy with the overall direction of the agency, according to internal documents examined by ProPublica. The records show nearly 4 in 10 of the roughly 2,000 doctors offered jobs from January through March of this year turned them down. That is quadruple the rate of doctors rejecting offers during the same time period last year.</p>
                        <p data-pp-blocktype="copy" data-pp-id="2.2">The VA in March said it intended to cut its workforce by at least 70,000 people. The news sparked alarm that the cuts would hurt patient care, prompting public reassurances from VA Secretary Doug Collins that front-line health care staff would be immune from the proposed layoffs.</p>
                        <p data-pp-blocktype="copy" data-pp-id="2.3">Last month, department officials updated their plans and said they would reduce the workforce by 30,000 by the end of the fiscal year, which is Sept. 30. So many staffers had left voluntarily, the agency said in a press release, that mass layoffs would not be necessary.</p>
                        <p data-pp-blocktype="copy" data-pp-id="2.4">“VA is headed in the right direction,” <a href="https://news.va.gov/press-room/va-to-reduce-staff-by-nearly-30k-by-end-of-fy2025/">Collins said in a statement</a>.</p>
                        <p data-pp-blocktype="copy" data-pp-id="2.5">But a review of hundreds of internal staffing records, along with interviews with veterans and employees, reveal a far less rosy picture of how staffing is affecting veterans’ care.</p>   
                        <p data-pp-blocktype="copy" data-pp-id="4.0">After six years of adding medical staff, the VA this year is down more than 600 doctors and about 1,900 nurses. The number of doctors on staff has declined each month since President Donald Trump took office. The agency also lost twice as many nurses as it hired between January and June, records viewed by ProPublica show.</p>
                        <p data-pp-blocktype="copy" data-pp-id="4.1">In response to questions, a VA spokesperson did not dispute numbers about staff losses at centers across the country but accused ProPublica of bias and of “cherry-picking issues that are mostly routine.”</p>
                        <p data-pp-blocktype="copy" data-pp-id="4.2">Agency spokesperson Peter Kasperowicz said that the department is “working to address” the number of doctors declining job offers by speeding up the hiring process and that the agency “has several strategies to navigate shortages,” including referring veterans to private providers and telehealth appointments. A nationwide shortage of health care workers has made hiring and retention difficult, he said.</p>
                        <p data-pp-blocktype="copy" data-pp-id="4.3">Kasperowicz said that the recent changes at the agency have not compromised care and that wait times are getting better after worsening under President Joe Biden.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.4">While wait times for primary, mental health and specialty care for existing patients did increase during Biden’s presidency, the VA’s statistics show only slight reductions since Trump took office in January.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.5">However, appointment wait times for new patients seeking primary and specialty care have slightly increased, according to a report obtained by ProPublica.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.6">As of early July, the average wait time nationally to schedule outpatient surgery appointments for new patients was 41 days, which is 13 days higher than the goal set by the VA and nearly two days longer than a year ago.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.7">In some locations, the waits for appointments are even longer.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.8">At the Togus VA Medical Center in Augusta, Maine, internal records show that there is a two-month wait for primary care appointments, which is triple the VA’s goal and 38 days longer than it was at this time last year. The wife of a disabled Marine veteran who receives care at the facility told ProPublica that it has become harder in recent months to schedule appointments and to get timely care.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.9">Her husband, she said, served in Somalia and is completely disabled. He has not had a primary care doctor assigned to him for months after his previous doctor left over the winter, she said.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.10">“He has no person who is in charge of his health care,” said the woman, who did not want to be named because of fears her comments might affect benefits for her husband. “It was never like this before. There’s a lack of staff, empty rooms, locked doors. It feels like something that’s not healthy.”</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.11">Kasperowicz said the VA is taking “aggressive action” to recruit primary care doctors in Maine and anticipates hiring two new doctors by the end of the year.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.12">Nationwide, records reviewed by ProPublica show, the vacancy rate for doctors at the VA was 13.7% in May, up from 12% in May of 2024. Kasperowicz said those rates are in line with historical averages for the agency. But while the vacancy rate decreased over the first five months of 2024, it has risen in 2025.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.13">Sen. Richard Blumenthal, D-Conn., who has been critical of Collins’ stewardship, has argued that the VA is heading in a dangerous new direction. He said that ProPublica’s findings reinforce his concerns about “damaging and dangerous impacts” from cuts and staffing reductions.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.14">“Dedicated professionals are fleeing — and recruitment is flagging — because of toxic work conditions and draconian funding cuts and firings,” he told ProPublica. “We’ve warned repeatedly about these results — shocking, but not surprising.”</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.15">In the VA’s Texas region, which covers most of the state, officials reported in an internal presentation in June that approximately 90 people had turned down job offers “due to the uncertainty of reorganization” and noted that low morale was causing existing employees to not recommend working at the medical centers.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.16">Anthony Martinez, a retired Army captain who did tours of duty in Iraq and Afghanistan, said he has witnessed a downgrade in care at the Temple, Texas, VA facility. He said that the hospital has lost records of his recent allergy shots, which he now has to repeat, and he has to wait longer for appointments.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.17">“Problems have always existed but not to this degree,” Martinez said.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.18">Martinez, who runs a local nonprofit for veterans, said he’s heard similar frustrations from many of them. “It’s not just me. Many vets are having bad experiences,” he said.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.19">Kasperowicz said the agency couldn’t discuss Martinez’s case without a patient privacy waiver, which Martinez declined to sign. He said wait times for primary care appointments for existing patients at Temple are unchanged over the past fiscal year. But internal records show an increase in wait times for new patients in specialties such as cardiology, gastroenterology and oncology.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.20">Administrators there have expressed concern about the impact of staff losses, warning in their June internal presentation about “institutional knowledge leaving the Agency due to the increase of supervisors departing.”</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.21">It is not just the loss of doctors and nurses impacting care. Shortages in support staff, who have not been protected from cuts, are also adding to delays.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.22">In Dayton, Ohio, vacant positions for purchasing agents resulted in delays in acquiring hundreds of prosthetics, according to an internal VA report from May. Kasperowicz said the hospital has recently cut processing time for such orders by more than half.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.23">Some facilities are experiencing trouble hiring and keeping mental health staff.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.24">In February, a human resources official in the VA region covering much of Florida reported in an internal warning system that the area was having trouble hiring mental health professionals to treat patients in rural areas. The jobs had previously been entirely remote but now require providers to be on site at a clinic.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.25">When the region offered jobs to three mental health providers, all of them declined. The expected impact, according to the warning document, was longer delays for appointments. Kasperowicz said the VA is working to address the shortages.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.26">Yet even as the agency faces these challenges, the Trump administration has dramatically scaled back the use of a key tool designed to help the VA attract applicants and plug gaps in critical front-line care.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.27">The VA in recent years has used incentive payments to help recruit and keep doctors and other health care workers. In fiscal 2024, the agency paid nearly 20,000 staffers retention bonuses and over 6,000 new hires got signing bonuses. In the first nine months of this fiscal year, which started Oct. 1, only about 8,000 VA employees got retention bonuses and just over 1,000 received recruitment incentives. The VA has told lawmakers it has been able to fill jobs without using the incentive programs.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.28">Rep. Delia Ramirez, D-Ill., said during a congressional oversight hearing in July that the Trump administration is withholding the bonuses because it “wants them to leave” as part of a plan to privatize services.</p>

                        <p data-pp-blocktype="copy" data-pp-id="4.29">“It’s not that VA employees are less meritorious than they were under Biden,” she said. “They want every employee to be pushed out so they can decimate the VA’s workforce.”</p>

                        <p>Do you have information about the VA that we should know about? Contact reporters David Armstrong on Signal, DavidArmstrong.55, or via email, <a href="mailto:david.armstrong@propublica.org">david.armstrong@propublica.org</a>; Eric Umansky on Signal, <a href="https://signal.me/#eu/9FeeZ1BCD0kQTLZOiK3bE2QbcfD_GzYCm-O_EP3CL7X3ZsIp14NI7pkzYjshgk7a">Ericumansky.04</a>, or via email, <a href="mailto:eric.umansky@propublica.org">eric.umansky@propublica.org</a>; and Vernal Coleman on Signal, vcoleman91.99, or via email, <a href="mailto:vernal.coleman@propublica.org">vernal.coleman@propublica.org</a>.</p>

                        <p><a href="https://www.propublica.org/people/joel-jacobs">Joel Jacobs</a> contributed reporting.</p>

        

    
        """;
    }
}
