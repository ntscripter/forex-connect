using System;
using System.Collections.Generic;
using ArgParser;
using fxcore2;

namespace CreateOTO
{
    class Program
    {
        static void Main(string[] args)
        {
            O2GSession session = null;
            SessionStatusListener statusListener = null;
            ResponseListener responseListener = null;

            try
            {
                Console.WriteLine("CreateOTO sample\n");

                ArgumentParser argParser = new ArgumentParser(args, "CreateOTO");

                argParser.AddArguments(ParserArgument.Login,
                                       ParserArgument.Password,
                                       ParserArgument.Url,
                                       ParserArgument.Connection,
                                       ParserArgument.SessionID,
                                       ParserArgument.Pin,
                                       ParserArgument.Instrument,
                                       ParserArgument.Lots,
                                       ParserArgument.AccountID);

                argParser.ParseArguments();

                if (!argParser.AreArgumentsValid)
                {
                    argParser.PrintUsage();
                    return;
                }

                argParser.PrintArguments();

                LoginParams loginParams = argParser.LoginParams;
                SampleParams sampleParams = argParser.SampleParams;

                session = O2GTransport.createSession();
                statusListener = new SessionStatusListener(session, loginParams.SessionID, loginParams.Pin);
                session.subscribeSessionStatus(statusListener);
                session.login(loginParams.Login, loginParams.Password, loginParams.URL, loginParams.Connection);
                if (statusListener.WaitEvents() && statusListener.Connected)
                {
                    responseListener = new ResponseListener(session);
                    session.subscribeResponse(responseListener);

                    O2GAccountRow account = GetAccount(session, sampleParams.AccountID);
                    if (account == null)
                    {
                        if (string.IsNullOrEmpty(sampleParams.AccountID))
                        {
                            throw new Exception("No valid accounts");
                        }
                        else
                        {
                            throw new Exception(string.Format("The account '{0}' is not valid", sampleParams.AccountID));
                        }
                    }
                    sampleParams.AccountID = account.AccountID;

                    O2GOfferRow offer = GetOffer(session, sampleParams.Instrument);
                    if (offer == null)
                    {
                        throw new Exception(string.Format("The instrument '{0}' is not valid", sampleParams.Instrument));
                    }

                    O2GLoginRules loginRules = session.getLoginRules();
                    if (loginRules == null)
                    {
                        throw new Exception("Cannot get login rules");
                    }

                    O2GTradingSettingsProvider tradingSettingsProvider = loginRules.getTradingSettingsProvider();
                    int iBaseUnitSize = tradingSettingsProvider.getBaseUnitSize(sampleParams.Instrument, account);
                    int iAmount = iBaseUnitSize * sampleParams.Lots;

                    double dRatePrimary = offer.Bid - 30.0 * offer.PointSize;
                    double dRateOcoFirst = offer.Ask + 15.0 * offer.PointSize;
                    double dRateOcoSecond = dRatePrimary - 15.0 * offer.PointSize;

                    O2GRequest request = CreateOTOCORequest(session, offer.OfferID, account.AccountID, iAmount, dRatePrimary, dRateOcoFirst, dRateOcoSecond);
                    if (request == null)
                    {
                        throw new Exception("Cannot create request");
                    }

                    List<string> requestIDList = new List<string>();
                    FillRequestIDs(requestIDList, request); 
                    
                    responseListener.SetRequestIDs(requestIDList);
                    session.sendRequest(request);
                    if (responseListener.WaitEvents())
                    {
                        Console.WriteLine("Done!");
                    }
                    else
                    {
                        throw new Exception("Response waiting timeout expired");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: {0}", e.ToString());
            }
            finally
            {
                if (session != null)
                {
                    if (statusListener.Connected)
                    {
                        statusListener.Reset();
                        session.logout();
                        statusListener.WaitEvents();
                        if (responseListener != null)
                            session.unsubscribeResponse(responseListener);
                    }
                    session.unsubscribeSessionStatus(statusListener);
                    session.Dispose();
                }
            }
        }

        /// <summary>
        /// Find valid account
        /// </summary>
        /// <param name="session"></param>
        /// <returns>account</returns>
        private static O2GAccountRow GetAccount(O2GSession session, string sAccountID)
        {
            O2GAccountRow account = null;
            bool bHasAccount = false;
            O2GResponseReaderFactory readerFactory = session.getResponseReaderFactory();
            if (readerFactory == null)
            {
                throw new Exception("Cannot create response reader factory");
            }
            O2GLoginRules loginRules = session.getLoginRules();
            O2GResponse response = loginRules.getTableRefreshResponse(O2GTableType.Accounts);
            O2GAccountsTableResponseReader accountsResponseReader = readerFactory.createAccountsTableReader(response);
            for (int i = 0; i < accountsResponseReader.Count; i++)
            {
                account = accountsResponseReader.getRow(i);
                string sAccountKind = account.AccountKind;
                if (sAccountKind.Equals("32") || sAccountKind.Equals("36"))
                {
                    if (account.MarginCallFlag.Equals("N"))
                    {
                        if (string.IsNullOrEmpty(sAccountID) || sAccountID.Equals(account.AccountID))
                        {
                            bHasAccount = true;
                            break;
                        }
                    }
                }
            }
            if (!bHasAccount)
            {
                return null;
            }
            else
            {
                return account;
            }
        }

        /// <summary>
        /// Find valid offer by instrument name
        /// </summary>
        /// <param name="session"></param>
        /// <param name="sInstrument"></param>
        /// <returns>offer</returns>
        private static O2GOfferRow GetOffer(O2GSession session, string sInstrument)
        {
            O2GOfferRow offer = null;
            bool bHasOffer = false;
            O2GResponseReaderFactory readerFactory = session.getResponseReaderFactory();
            if (readerFactory == null)
            {
                throw new Exception("Cannot create response reader factory");
            }
            O2GLoginRules loginRules = session.getLoginRules();
            O2GResponse response = loginRules.getTableRefreshResponse(O2GTableType.Offers);
            O2GOffersTableResponseReader offersResponseReader = readerFactory.createOffersTableReader(response);
            for (int i = 0; i < offersResponseReader.Count; i++)
            {
                offer = offersResponseReader.getRow(i);
                if (offer.Instrument.Equals(sInstrument))
                {
                    if (offer.SubscriptionStatus.Equals("T"))
                    {
                        bHasOffer = true;
                        break;
                    }
                }
            }
            if (!bHasOffer)
            {
                return null;
            }
            else
            {
                return offer;
            }
        }

        /// <summary>
        /// Create OTO request
        /// </summary>
        private static O2GRequest CreateOTOCORequest(O2GSession session, string sOfferID, string sAccountID, int iAmount, double dRatePrimary, double dRateOcoFirst, double dRateOcoSecond)
        {
            O2GRequest request = null;
            O2GRequestFactory requestFactory = session.getRequestFactory();
            if (requestFactory == null)
            {
                throw new Exception("Cannot create request factory");
            }

            // Create OTO command
            O2GValueMap valuemapMain = requestFactory.createValueMap();
            valuemapMain.setString(O2GRequestParamsEnum.Command, Constants.Commands.CreateOTO);

            // Create Entry order
            O2GValueMap valuemapPrimary = requestFactory.createValueMap();
            valuemapPrimary.setString(O2GRequestParamsEnum.Command, Constants.Commands.CreateOrder);
            valuemapPrimary.setString(O2GRequestParamsEnum.OrderType, Constants.Orders.StopEntry);
            valuemapPrimary.setString(O2GRequestParamsEnum.AccountID, sAccountID);
            valuemapPrimary.setString(O2GRequestParamsEnum.OfferID, sOfferID);
            valuemapPrimary.setString(O2GRequestParamsEnum.BuySell, Constants.Sell);
            valuemapPrimary.setInt(O2GRequestParamsEnum.Amount, iAmount);
            valuemapPrimary.setDouble(O2GRequestParamsEnum.Rate, dRatePrimary);

            // Create OCO group of orders
            O2GValueMap valuemapOCO = requestFactory.createValueMap();
            valuemapOCO.setString(O2GRequestParamsEnum.Command, Constants.Commands.CreateOCO);

            // Create Entry order to OCO
            O2GValueMap valuemapOCOFirst = requestFactory.createValueMap();
            valuemapOCOFirst.setString(O2GRequestParamsEnum.Command, Constants.Commands.CreateOrder);
            valuemapOCOFirst.setString(O2GRequestParamsEnum.OrderType, Constants.Orders.StopEntry);
            valuemapOCOFirst.setString(O2GRequestParamsEnum.AccountID, sAccountID);
            valuemapOCOFirst.setString(O2GRequestParamsEnum.OfferID, sOfferID);
            valuemapOCOFirst.setString(O2GRequestParamsEnum.BuySell, Constants.Buy);
            valuemapOCOFirst.setInt(O2GRequestParamsEnum.Amount, iAmount);
            valuemapOCOFirst.setDouble(O2GRequestParamsEnum.Rate, dRateOcoFirst);

            O2GValueMap valuemapOCOSecond = requestFactory.createValueMap();
            valuemapOCOSecond.setString(O2GRequestParamsEnum.Command, Constants.Commands.CreateOrder);
            valuemapOCOSecond.setString(O2GRequestParamsEnum.OrderType, Constants.Orders.StopEntry);
            valuemapOCOSecond.setString(O2GRequestParamsEnum.AccountID, sAccountID);
            valuemapOCOSecond.setString(O2GRequestParamsEnum.OfferID, sOfferID);
            valuemapOCOSecond.setString(O2GRequestParamsEnum.BuySell, Constants.Sell);
            valuemapOCOSecond.setInt(O2GRequestParamsEnum.Amount, iAmount);
            valuemapOCOSecond.setDouble(O2GRequestParamsEnum.Rate, dRateOcoSecond);

            // Fill the created groups. Please note, first you should add an entry order to OTO order and then OCO group of orders
            valuemapMain.appendChild(valuemapPrimary);
            valuemapOCO.appendChild(valuemapOCOFirst);
            valuemapOCO.appendChild(valuemapOCOSecond);
            valuemapMain.appendChild(valuemapOCO);

            request = requestFactory.createOrderRequest(valuemapMain);
            if (request == null)
            {
                Console.WriteLine(requestFactory.getLastError());
            }
            return request;
        }
        
        /// <summary>
        /// Fill request IDs
        /// </summary>
        /// <param name="requestIDs">List of request IDs</param>
        /// <param name="request">request</param>
        private static void FillRequestIDs(List<string> requestIDs, O2GRequest request)
        {
            int childrenCount = request.ChildrenCount;
            if (childrenCount == 0)
            {
                requestIDs.Add(request.RequestID);
                return;
            }

            for (int i = 0; i < childrenCount; i++)
            {
                O2GRequest childRequest = request.getChildRequest(i);
                FillRequestIDs(requestIDs, childRequest);
            }
        }
    }
}
