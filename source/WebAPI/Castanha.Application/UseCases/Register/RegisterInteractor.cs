﻿namespace Castanha.Application.UseCases.Register
{
    using System.Threading.Tasks;
    using Castanha.Domain.Customers;
    using Castanha.Domain.ValueObjects;
    using Castanha.Application.Outputs;
    using Castanha.Application.ServiceBus;
    using Castanha.Domain.Accounts;
    using Castanha.Application.Repositories;
    using System.Threading;
    using System;

    public class RegisterInteractor : IInputBoundary<RegisterInput>
    {
        private readonly ICustomerReadOnlyRepository customerReadOnlyRepository;
        private readonly IAccountReadOnlyRepository accountReadOnlyRepository;
        private readonly IPublisher bus;
        private readonly IOutputBoundary<RegisterOutput> outputBoundary;
        private readonly IOutputConverter responseConverter;
        
        public RegisterInteractor(
            ICustomerReadOnlyRepository customerReadOnlyRepository,
            IAccountReadOnlyRepository accountReadOnlyRepository,
            IPublisher bus,
            IOutputBoundary<RegisterOutput> outputBoundary,
            IOutputConverter responseConverter)
        {
            this.customerReadOnlyRepository = customerReadOnlyRepository;
            this.accountReadOnlyRepository = accountReadOnlyRepository;
            this.bus = bus;
            this.outputBoundary = outputBoundary;
            this.responseConverter = responseConverter;
        }

        public async Task Process(RegisterInput message)
        {
            Customer customer = new Customer(message.PIN, message.Name);

            Account account = new Account();
            account.Open(customer.Id, new Credit(account.Id, message.InitialAmount));
            customer.Register(account.Id);

            var customerEvents = customer.GetEvents();
            var accountEvents = account.GetEvents();

            await bus.Publish(customerEvents);
            await bus.Publish(accountEvents);

            //
            // To ensure the Customer and Account are created in the database
            // we wait for the records be available in the following queries
            // with retry 
            //

            bool consumerReady = await RetryGet(async() => await customerReadOnlyRepository.Get(customer.Id)) && 
                await RetryGet(async () => await accountReadOnlyRepository.Get(account.Id));

            if (!consumerReady)
            {
                customer = null;
                account = null;

                //
                // TODO: Throw exception, monitor the inconsistencies and fail fast.
                //
            }

            CustomerOutput customerOutput = responseConverter.Map<CustomerOutput>(customer);
            AccountOutput accountOutput = responseConverter.Map<AccountOutput>(account);
            RegisterOutput output = new RegisterOutput(customerOutput, accountOutput);

            outputBoundary.Populate(output);
        }

        private async Task<bool> RetryGet(Func<Task<object>> repository)
        {
            object data = null;
            int count = 1000;
            do
            {
                data = await repository();
                if (data != null)
                    break;

                Thread.Sleep(count);

                count = count + count;
            } while (data == null && count < 6000);

            if (data != null)
                return true;
            else
                return false;
        }
    }
}
