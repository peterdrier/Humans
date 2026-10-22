using Humans.Base.Interfaces;
using Humans.Campaigns.Contracts;

namespace Humans.Campaigns.Services;

internal interface ICampaignManagement : ICampaignService, ICampaignWrite, IApplicationService;
