using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.Web.Mvc;
using ExploreWiki.Helpers;
using ExploreWiki.Models;
using ExploreWiki.ViewModel;
using Newtonsoft.Json;
using QC = System.Data.SqlClient;  // System.Data.dll  
using DT = System.Data;

namespace ExploreWiki.Controllers
{
    public class HomeController : Controller
    {
        /// <summary>
        /// Max number of nodes in graph.
        /// </summary>
        private const int MaxNumberOfPersonsPerGraph = 100;

        private const string DefaultProviderName = "System.Data.ProdDb";
        
        /// <summary>
        /// List of all connections in graph.
        /// </summary>
        List<Relation> PersonConnections { get; set; }

        /// <summary>
        /// List of all nodes in graph.
        /// </summary>
        Dictionary<string, Person> Persons { get; set; }

        Queue<Person> processingQueue { get; set; }

        /// <summary>
        /// Generating helper message from the time needed to generate the graph.
        /// </summary>
        TimeSpan GenerationTook { get; set; }
       

        /// <summary>
        /// Action which builds the home page.
        /// </summary>
        /// <param name="personViewModel">Input name from the main form.</param>
        /// <returns></returns>
        public ActionResult Index(PersonViewModel personViewModel)
        {
            Persons = new Dictionary<string, Person>();
            PersonConnections = new List<Relation>();
            processingQueue = new Queue<Person>();
            ViewBag.PersonNames = Persons;
            ViewBag.PersonConnections = PersonConnections;
            ViewBag.Message = "Enter person name to start graph traversal.";

            if (String.IsNullOrEmpty(personViewModel.InputName))
            {
                // Nothing to do here. Waiting for user input.
                return View();
            }

            ViewBag.InputName = personViewModel.InputName;

            Person startingPerson = new Person(personViewModel.InputName.Denormalize(), 0);
            Persons.Add(startingPerson.Name, startingPerson);

            Stopwatch sw = new Stopwatch();
            sw.Start();
            BuildGraph(startingPerson, 1, true, false);

            if (Persons.Count < 60)
            {
                // Restart search in aggressive mode.
                Persons.Clear();
                PersonConnections.Clear();
                BuildGraph(startingPerson, 1, true, true, true);
            }

            GenerationTook = sw.Elapsed;

            ViewBag.PersonsJson = JsonConvert.SerializeObject(Persons, Formatting.Indented);
            ViewBag.PersonsConnectionsJson = JsonConvert.SerializeObject(PersonConnections, Formatting.Indented);
            
            ViewBag.PersonNames = Persons;
            ViewBag.PersonConnections = PersonConnections;
            ViewBag.GenerationTook = string.Format("{0}s for generating graph of {1} nodes.", GenerationTook.TotalSeconds, Persons.Count);
            return View(personViewModel);
        }

        
        /// <summary>
        /// Empty for now.
        /// </summary>
        /// <param name="personViewModel"></param>
        /// <returns></returns>
        public ActionResult About(PersonViewModel personViewModel)
        {
            return View();
        }


        /// <summary>
        /// Empty for now.
        /// </summary>
        /// <returns></returns>
        public ActionResult Contact()
        {
            ViewBag.Title = "I should write something here but I am too lazy.";

            return View();
        }

        /// <summary>
        /// Auto complete for user inputs.
        /// </summary>
        /// <param name="term">Search pattern we are using for.</param>
        /// <returns></returns>
        [HttpPost]
        public ActionResult Autocomplete(string term)
        {
            // Pulling this from database is a bit too slow.
            List<string> autocompleteItems = new List<string>();
            using (var connection = new QC.SqlConnection(GetConnectionString(DefaultProviderName)))
            {
                connection.Open();
                using (var command = new QC.SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandType = DT.CommandType.Text;
                    command.CommandText = @"  
                        select top 10 name from persons1 
                        where name like @name
                    ";

                    command.Parameters.AddWithValue("@name", term.Denormalize() + "%");

                    QC.SqlDataReader reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        autocompleteItems.Add(reader.GetString(0).Normalize());
                    }
                }
            }

            return Json(autocompleteItems.ToArray(), JsonRequestBehavior.AllowGet);
        }

        #region private_helper_methods
        private string GetConnectionString(string providerName)
        {
            ConnectionStringSettingsCollection settings = ConfigurationManager.ConnectionStrings;
            string returnValue = null;

            if (settings != null)
            {
                foreach (ConnectionStringSettings cs in settings)
                {
                    if (cs.ProviderName == providerName)
                        returnValue = cs.ConnectionString;
                    break;
                }
            }

            return returnValue;
        }

        /// <summary>
        /// Main graph build method.
        /// Recursively builds the graph either until the size suffices or we are out of the nodes.
        /// </summary>
        /// <param name="startingPerson"></param>
        /// <param name="depth"></param>
        /// <param name="includePointingTo"></param>
        /// <param name="includeNonPersons"></param>
        /// <param name="aggressive"></param>
        private void BuildGraph(Person startingPerson, int depth, bool includePointingTo, bool includeNonPersons, bool aggressive = false)
        {
            // TODO: Don't open new connection every time.
            // Use connection pool.
            using (var connection = new QC.SqlConnection(GetConnectionString(DefaultProviderName)))
            {
                connection.Open();
                using (var command = new QC.SqlCommand())
                {
                    command.Connection = connection;
                    command.CommandType = DT.CommandType.Text;

                    // Queries here are not very optimized. Do better.
                    if (includePointingTo && !includeNonPersons)
                    {
                        command.CommandText = @"  
                            select entity_name as node_from, property_name as branch_name, link2 as node_to from properties1 
                            where link2 = @name and  exists (select name from persons where name = entity_name)
                            select entity_name as node_from, property_name as branch_name, link2 as node_to from properties1 
                            where entity_name = @name and link2 is not null and isPerson = 1
                        ";
                    }
                    else if (!includePointingTo && includeNonPersons)
                    {

                        command.CommandText = @"  
                            select entity_name as node_from, property_name as branch_name, link2 as node_to from properties1 
                            where entity_name = @name and link2 is not null
                         ";
                    }
                    else if (includePointingTo && includeNonPersons)
                    {
                        command.CommandText = @"
                            select entity_name as node_from, property_name as branch_name, link2 as node_to from properties1 
                            where (link2 = @name) or (entity_name = @name and link2 is not null)
                        ";
                    }
                    else
                    {

                        command.CommandText = @"  
                            select entity_name as node_from, property_name as branch_name, link2 as node_to from properties1 
                            where entity_name = @name and link2 is not null and isPerson = 1
                        ";
                    }

                    command.Parameters.AddWithValue("@name", startingPerson.Name);

                    QC.SqlDataReader reader = command.ExecuteReader();

                    while (reader.Read())
                    {
                        Person personFrom;
                        Person personTo;

                        string nodeFrom = reader.GetString(0);
                        string branchName = reader.GetString(1);
                        string nodeTo = reader.GetString(2);

                        bool isPersonFromNew = false;
                        bool isPersonToNew = false;

                        if (!Persons.TryGetValue(nodeFrom, out personFrom))
                        {
                            personFrom = new Person(nodeFrom, depth);
                            Persons.Add(personFrom.Name, personFrom);
                            isPersonFromNew = true;
                            processingQueue.Enqueue(personFrom);
                        }

                        if (!Persons.TryGetValue(nodeTo, out personTo))
                        {
                            personTo = new Person(nodeTo, depth);
                            Persons.Add(personTo.Name, personTo);
                            isPersonToNew = true;
                            processingQueue.Enqueue(personTo);
                        }

                        if (isPersonFromNew)
                        {
                            personFrom.DistanceFromCenter = personTo.DistanceFromCenter + 1;
                        }

                        if (isPersonToNew)
                        {
                            personTo.DistanceFromCenter = personFrom.DistanceFromCenter + 1;
                        }

                        PersonConnections.Add(new Relation(personFrom, personTo, branchName));
                    }
                }
            }

            // One more check, if we are early in the graph and we still don't have many results
            // do more aggressive search.
            if (depth == 1 && Persons.Count < 5 && !includePointingTo && !includeNonPersons)
            {
                if (!includePointingTo)
                {
                    BuildGraph(startingPerson, 1, true, false);
                }
                else
                {
                    // Do full search.
                    BuildGraph(startingPerson, 1, true, true);
                }
            }

            while (processingQueue.Count != 0 && Persons.Count < MaxNumberOfPersonsPerGraph)
            {
                if (aggressive && depth < 3)
                {
                    BuildGraph(processingQueue.Dequeue(), depth + 1, true, true, aggressive);
                }
                else if (aggressive && depth < 4)
                {
                    BuildGraph(processingQueue.Dequeue(), depth + 1, true, false, aggressive);
                }
                if (depth < 3 && processingQueue.Count < 30)
                {
                    BuildGraph(processingQueue.Dequeue(), depth + 1, true, false);
                }
                else
                {
                    BuildGraph(processingQueue.Dequeue(), depth + 1, false, false);
                }
            }
        }
        #endregion
    }
}