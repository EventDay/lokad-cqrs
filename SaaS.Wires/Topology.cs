using System;
using System.Linq;
using Lokad.Cqrs;
using Lokad.Cqrs.Envelope;
using Lokad.Cqrs.Partition;
using Lokad.Cqrs.TapeStorage;

namespace SaaS.Wires
{
    public static class Topology
    {
        public const string EventsQueue = "sample-queue-events";
        public const string EntityQueue = "sample-queue-entity";
        public const string DomainLogName = "domain.tmd";
        public const string ServiceQueue = "sample-queue-service";
        public const string RouterQueue = "sample-queue-router";

        public const string TapesContainer = "sample-tapes";

        public static Action<ImmutableEnvelope> Route(Func<string, IQueueWriter> factory, IEnvelopeStreamer serializer,
            IAppendOnlyStore tape)
        {
            var events = factory(EventsQueue);
            var entityQueue = factory(EntityQueue);
            var services = factory(ServiceQueue);

           
            
            
            return envelope =>
                {
                    var data = serializer.SaveEnvelopeData(envelope);
                    tape.Append("audit", data, -1);


                    if (envelope.Items.All(i => i.Content is ICommand<IIdentity>))
                    {
                        entityQueue.PutMessage(data);
                        return;
                    }
                    if (envelope.Items.All(i => i.Content is IEvent<IIdentity>))
                    {
                        // we can have more than 1 entity event.
                        // all entity events are routed to events as separate
                        for (int i = 0; i < envelope.Items.Length; i++)
                        {
                            var name = envelope.EnvelopeId + "-e" + i;
                            var copy = EnvelopeBuilder.CloneProperties(name, envelope);
                            copy.AddItem(envelope.Items[i]);
                            events.PutMessage(serializer.SaveEnvelopeData(copy.Build()));
                        }
                        return;
                    }

                    if (envelope.Items.Length != 1)
                    {
                        throw new InvalidOperationException(
                            "Only entity commands or entity events can be batched");
                    }
                    var item = envelope.Items[0].Content;
                    if (item is IFunctionalCommand)
                    {
                        services.PutMessage(data);
                        return;
                    }
                    if (item is IFunctionalEvent || item is IEvent)
                    {
                        events.PutMessage(data);
                        return;
                    }
                    throw new InvalidOperationException(string.Format("Unroutable message {0}", item));
                };
        }
    }
}